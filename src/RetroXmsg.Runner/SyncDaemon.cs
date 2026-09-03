using System;
using System.Collections.Generic;
using System.IO;

using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Sync;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Live;
namespace RetroXmsg.Runner
{
    /// <summary>
    /// Watches one folder and keeps it mirrored onto a SINTRAN user directory.
    /// </summary>
    /// <remarks>
    /// <para><b>What it does each cycle</b></para>
    /// <para>
    /// Reads the folder and hashes what is in it, tells the settle tracker what it saw, asks the
    /// planner what should happen, and hands the result to the queue. Then, every tick, it moves
    /// whatever transfer is running along by one step.
    /// </para>
    /// <para><b>Scanning is on a timer, carrying is on the tick</b></para>
    /// <para>
    /// Those are different rates on purpose. Scanning costs a hash of every file, which is worth
    /// doing every few seconds and not every millisecond. Carrying has to happen on the node's own
    /// loop tick, because that same tick answers the machine at the other end - a transfer that
    /// blocked would stop the file server replying and time the far side out.
    /// </para>
    /// <para><b>It does not list the remote directory yet, and says so</b></para>
    /// <para>
    /// Getting a listing off the machine is a conversation, and running one between transfers
    /// would take its turn in the same single datagram sequence. Until that is built the daemon
    /// runs with an EMPTY listing.
    /// </para>
    /// <para>
    /// The planner no longer reads that as "the directory is empty". With no listing it asks the
    /// ledger instead, which holds an entry only for a file actually carried to or from this
    /// machine - so a file sent before is REPLACED rather than created again. Before that, every
    /// push after a restart went out as a create of a name that already existed, which SINTRAN
    /// refuses, and the first pass was a burst of failures that meant nothing.
    /// </para>
    /// <para>
    /// One case is still wrong and only a listing can fix it: a file deleted on the machine behind
    /// our back is still believed to exist, so it is replaced rather than created. That is stated
    /// rather than hidden.
    /// </para>
    /// </remarks>
    internal sealed class SyncDaemon
    {
        /// <summary>
        /// The value passed as the user to mean "this folder is a ROOT of user folders".
        /// </summary>
        /// <remarks>
        /// A marker rather than a separate constructor because everything else about the two
        /// shapes is identical, and an asterisk cannot collide with a SINTRAN user name.
        /// </remarks>
        public const string RootMarker = "*";

        /// <summary>
        /// One mapping per SINTRAN user this daemon serves.
        /// </summary>
        /// <remarks>
        /// A SINTRAN user directory is FLAT, so a local folder can only correspond to a user. One
        /// folder is therefore one user, and serving several users means several folders - which
        /// is what a root folder full of user-named subfolders gives you.
        /// </remarks>
        private readonly IReadOnlyList<SyncFolderMapping> _mappings;
        private readonly SyncPass _pass;
        private readonly SyncRunner _runner;
        private readonly LiveSyncTransferAgent _agent;
        private readonly TimeSpan _scanEvery;
        private readonly SyncLedger _ledger;
        private readonly string _ledgerPath;

        /// <summary>
        /// Where fetch requests are dropped, or null when the daemon only pushes.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a pull side exists at all</b></para>
        /// <para>
        /// The build loop used to run a SECOND, one-shot runner to fetch a compiler listing, and a
        /// one-shot ends by sending <c>DISC</c>. That is a link teardown, and a link teardown after
        /// a transfer is what kills the peer's XMSG - measured 2026-08-22, fourteen deaths out of
        /// fourteen attempts. It also could not run at all while this daemon held the link.
        /// </para>
        /// <para>
        /// So the fetch rides the link that is already open. One connect at the start of a session
        /// and one disconnect at the end, instead of one of each per change.
        /// </para>
        /// </remarks>
        private readonly string? _pullFolder;

        private DateTime _lastScan = DateTime.MinValue;
        private DateTime _lastStallReport = DateTime.MinValue;

        /// <summary>
        /// Builds a daemon for one folder.
        /// </summary>
        /// <param name="folder">
        /// The local folder to mirror.
        /// </param>
        /// <param name="machine">
        /// The machine name, as the connect letter addresses it.
        /// </param>
        /// <param name="user">
        /// The SINTRAN user whose directory receives the files.
        /// </param>
        /// <param name="nodeHost">
        /// The node that carries the transfers.
        /// </param>
        /// <param name="serverNode">
        /// The machine's node number.
        /// </param>
        /// <param name="linkReady">
        /// Answers whether the link can carry a frame we originate.
        /// </param>
        /// <param name="quietPeriod">
        /// How long a file must stop changing before it is offered.
        /// </param>
        /// <param name="scanEvery">
        /// How often the folder is read.
        /// </param>
        /// <param name="pullFolder">
        /// Where fetch requests are dropped, or null to push only. A file whose name ends
        /// <c>.req</c>, and whose single line is a remote filespec, fetches that file to a file
        /// beside it carrying the same name with the <c>.req</c> removed.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any reference argument other than <paramref name="pullFolder"/> is null.
        /// </exception>
        public SyncDaemon(
            string folder,
            string machine,
            string user,
            XmsgNodeHost nodeHost,
            ushort serverNode,
            Func<bool> linkReady,
            TimeSpan quietPeriod,
            TimeSpan scanEvery,
            string? pullFolder)
        {
            if (folder == null) { throw new ArgumentNullException(nameof(folder)); }
            if (machine == null) { throw new ArgumentNullException(nameof(machine)); }
            if (user == null) { throw new ArgumentNullException(nameof(user)); }

            _scanEvery = scanEvery;
            _pullFolder = pullFolder;

            // ONE FOLDER PER USER. When "user" names a real user this is the single-user shape
            // the command line has always had. When it is the ROOT marker, every subfolder of
            // "folder" is taken to be a user name and gets its own mapping - so one daemon serves
            // everybody, which is the only way a shared service can put a file in the right
            // directory.
            List<SyncFolderMapping> mappings = new List<SyncFolderMapping>();
            SyncFolderMap map = new SyncFolderMap();

            if (user == RootMarker)
            {
                string[] userFolders = System.IO.Directory.Exists(folder)
                    ? System.IO.Directory.GetDirectories(folder)
                    : Array.Empty<string>();

                for (int i = 0; i < userFolders.Length; i++)
                {
                    string name = System.IO.Path.GetFileName(userFolders[i]);

                    SyncFolderMapping perUser = new SyncFolderMapping(userFolders[i], machine, name);
                    perUser.Direction = SyncDirection.ToMachine;
                    mappings.Add(perUser);
                    map.Add(perUser);
                }

                if (mappings.Count == 0)
                {
                    // Loud, because the daemon would otherwise sit there scanning nothing and
                    // looking perfectly healthy.
                    Console.WriteLine(
                        "[sync] ROOT " + folder + " HAS NO USER FOLDERS. Make a subfolder named"
                        + " after a SINTRAN user - " + folder + "\\SYSTEM - and drop files in it.");
                }
            }
            else
            {
                SyncFolderMapping single = new SyncFolderMapping(folder, machine, user);
                single.Direction = SyncDirection.ToMachine;
                mappings.Add(single);
                map.Add(single);
            }

            _mappings = mappings;

            // WHAT HAS ALREADY BEEN CARRIED, remembered across restarts. Without this the answer
            // to "have we carried this content" is NO for everything after every restart, so the
            // whole folder is offered again - and against a machine that already has those files
            // each one is a CREATE of a name that exists, which SINTRAN refuses. The first pass
            // after a restart would be a burst of failures that mean nothing.
            _ledgerPath = System.IO.Path.Combine(AppContext.BaseDirectory, "xmsg-sync-ledger.state");
            SyncLedger ledger = SyncLedgerFile.Load(_ledgerPath);
            _ledger = ledger;
            Console.WriteLine($"[sync] ledger: {_ledgerPath} ({ledger.Count} file(s) already carried)");

            // The password list lives beside the executable and is optional: with no file, only
            // users that have no password can be written to, and the loader says so out loud.
            SyncCredentials credentials = SyncCredentials.Load(
                System.IO.Path.Combine(AppContext.BaseDirectory, "sync-credentials.txt"),
                line => Console.WriteLine(line));

            _agent = new LiveSyncTransferAgent(
                nodeHost, serverNode, machine, linkReady, credentials);
            _runner = new SyncRunner(_agent, ledger);
            _runner.Log = line => Console.WriteLine(line);

            _pass = new SyncPass(new SyncPlanner(map, ledger), new FileSettleTracker(quietPeriod.Ticks));
            _pass.Log = line => Console.WriteLine(line);

            Console.WriteLine(
                $"[sync] watching {folder} -> {machine}({user}), scanning every "
                + $"{scanEvery.TotalSeconds:F0}s, a file must be still for {quietPeriod.TotalSeconds:F0}s");
            Console.WriteLine(
                "[sync] the remote directory is NOT listed - nothing here asks the machine what it"
                + " holds. A file the ledger has carried before is taken to exist there and is"
                + " REPLACED; anything else is CREATED. A file deleted on the machine behind our"
                + " back is the one case that gets this wrong, and only a listing would catch it.");

            if (_pullFolder != null)
            {
                Directory.CreateDirectory(_pullFolder);
                Console.WriteLine(
                    $"[sync] fetch requests: drop <name>.req in {_pullFolder} holding one remote"
                    + " filespec (CHAT:LIST). The file arrives as <name> beside it and the .req"
                    + " becomes .done, or .failed with the reason.");
            }
        }

        /// <summary>
        /// Feeds a received datagram to a running transfer.
        /// </summary>
        /// <param name="frame">
        /// The frame the node received.
        /// </param>
        public void OnFrame(XmsgFrame frame)
        {
            _agent.OnFrame(frame);
        }

        /// <summary>
        /// Runs one tick: rescan when due, then move the queue along.
        /// </summary>
        /// <param name="now">
        /// The current time.
        /// </param>
        public void Pump(DateTime now)
        {
            if (now - _lastScan >= _scanEvery)
            {
                _lastScan = now;
                Scan(now);
                ScanPullRequests();
            }

            // A SYNC MUST NEVER TAKE THE NODE DOWN. This runs on the link's own loop tick, so an
            // exception escaping here kills the pump and every session on it. MEASURED 2026-08-11:
            // a file specification the open request refused threw out of Begin, and the whole
            // runner printed "pump error" and stopped - file server, TAD sessions and all, for one
            // bad name. A file that cannot be carried is a failed file; the node keeps serving.
            //
            // Order matters: the runner decides whether to START something, then the agent sends
            // the next frame of whatever is running. The other way round a transfer would sit idle
            // for a whole tick after being started.
            int doneBefore = _runner.Completed;

            // The ledger learns two different things, and only one of them is a completed transfer.
            int ledgerBefore = _ledger.Revision;

            try
            {
                _runner.Pump();
                _agent.Pump();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[sync] *** the transfer threw and was abandoned: {ex.Message}");
                Console.WriteLine("[sync] the node is still running; only that transfer stopped.");
                _agent.AbandonTransfer();
            }

            // Written the moment the ledger learns ANYTHING, not on a timer and not at shutdown:
            // a daemon is killed rather than asked to stop, so anything held until exit is lost
            // exactly when it matters.
            //
            // THE TEST USED TO BE "did a transfer complete", and that missed the other thing the
            // ledger learns. A create refused with "file already exists" teaches it that the file
            // is on the machine - no transfer completes, so nothing was written. MEASURED
            // 2026-08-18: the daemon learned it, was killed before the following overwrite
            // finished, and the fact was gone; the next run paid the same refusal again. Which is
            // exactly the case the comment above was written about.
            if (_runner.Completed != doneBefore || _ledger.Revision != ledgerBefore)
            {
                SyncLedgerFile.Save(_ledger, _ledgerPath);
            }

            // WHY THE WORK IS NOT MOVING. A queue that sits at "1 waiting" with no transfer
            // starting says the agent is not ready, and the agent has three separate reasons for
            // that - which one it is decides whether to look at the link, the peer or a transfer
            // that never finished. Reported every few seconds, not every tick.
            if (_runner.Queued > 0 && !_runner.Busy && now - _lastStallReport >= _scanEvery)
            {
                _lastStallReport = now;
                Console.WriteLine(
                    $"[sync] {_runner.Queued} transfer(s) waiting and none started: {_agent.WhyNotReady()}");
            }
        }

        /// <summary>
        /// Turns every waiting <c>.req</c> file into a queued fetch.
        /// </summary>
        /// <remarks>
        /// <para><b>The request file is RENAMED before the fetch is queued</b></para>
        /// <para>
        /// Not after, and not on completion. The scan runs every few seconds while a transfer of a
        /// 150KB source can take two minutes, so a request left in place would be queued again on
        /// every pass and the same file fetched a dozen times. Renaming first means a request is
        /// picked up exactly once, whatever happens to the transfer afterwards.
        /// </para>
        /// <para><b>A bad request is renamed too, with the reason written into it</b></para>
        /// <para>
        /// An empty file or one naming nothing addressable would otherwise be retried for ever, and
        /// a silent retry loop is the failure mode this project keeps paying for. The reason lands
        /// where the person who dropped the request will look.
        /// </para>
        /// </remarks>
        private void ScanPullRequests()
        {
            if (_pullFolder == null) { return; }

            string[] requests;
            try
            {
                requests = Directory.GetFiles(_pullFolder, "*.req");
            }
            catch (IOException)
            {
                return;
            }

            for (int i = 0; i < requests.Length; i++)
            {
                string reqPath = requests[i];
                string spec;
                try
                {
                    spec = File.ReadAllText(reqPath).Trim();
                }
                catch (IOException)
                {
                    // Still being written. Next pass.
                    continue;
                }

                // ".req" is four characters; what is left is where the file lands.
                string target = reqPath.Substring(0, reqPath.Length - 4);

                if (spec.Length == 0)
                {
                    Fail(reqPath, "the request file is empty - it must hold one remote filespec");
                    continue;
                }

                if (spec.IndexOf('\n') >= 0 || spec.IndexOf('\r') >= 0)
                {
                    Fail(reqPath, "one filespec per request file, and this one has more than a line");
                    continue;
                }

                // Renamed BEFORE queueing - see the note above.
                string taken = target + ".taken";
                try
                {
                    if (File.Exists(taken)) { File.Delete(taken); }
                    File.Move(reqPath, taken);
                }
                catch (IOException)
                {
                    continue;
                }

                List<SyncAction> actions = new List<SyncAction>();
                actions.Add(new SyncAction(SyncActionKind.Pull, target, spec, "asked for by a .req file"));
                // Every mapping targets the SAME machine - the daemon has one link - so any of
                // them names it. A fetch is not per-user: it asks for a file by specification.
                _runner.Enqueue(actions, _mappings[0].Machine);
                Console.WriteLine($"[sync] fetch {spec} -> {target} queued");
            }
        }

        /// <summary>
        /// Marks a request as refused, with the reason inside it.
        /// </summary>
        /// <param name="reqPath">
        /// The request file.
        /// </param>
        /// <param name="why">
        /// What was wrong with it, in a sentence.
        /// </param>
        private void Fail(string reqPath, string why)
        {
            Console.WriteLine($"[sync] fetch request {reqPath} refused: {why}");
            try
            {
                string failed = reqPath + ".failed";
                if (File.Exists(failed)) { File.Delete(failed); }
                File.WriteAllText(reqPath, why + Environment.NewLine);
                File.Move(reqPath, failed);
            }
            catch (IOException)
            {
                // Saying so on the console is enough; the request will be retried and refused
                // again, which is noisy but not wrong.
            }
        }

        /// <summary>
        /// Reads the folder and queues whatever the plan says.
        /// </summary>
        /// <param name="now">
        /// The current time, used for the settle clock.
        /// </param>
        private void Scan(DateTime now)
        {
            for (int m = 0; m < _mappings.Count; m++)
            {
                ScanOne(_mappings[m], now);
            }
        }

        /// <summary>
        /// Reads one user folder and queues whatever its plan says.
        /// </summary>
        /// <param name="mapping">
        /// The folder and the user it belongs to.
        /// </param>
        /// <param name="now">
        /// The current time, used for the settle clock.
        /// </param>
        /// <remarks>
        /// Each user is planned SEPARATELY on purpose. The plan compares a local file against the
        /// ledger for one user directory, and two users may legitimately hold the same file name -
        /// SINTRAN keeps them apart, and so must this.
        /// </remarks>
        private void ScanOne(SyncFolderMapping mapping, DateTime now)
        {
            List<string> unreadable = new List<string>();
            IReadOnlyList<LocalFileState> files =
                LocalFolderScanner.Scan(mapping.LocalFolder, mapping.Subfolders, unreadable);

            if (unreadable.Count > 0)
            {
                Console.WriteLine(
                    $"[sync] {unreadable.Count} file(s) could not be read this pass (something else"
                    + " has them open); they wait for the next one");
            }

            // Size and write time for the settle tracker. Taken separately from the hash because a
            // file can vanish between the two, and losing the whole pass to that would be silly.
            Dictionary<string, FileSizeAndTime> stamps = new Dictionary<string, FileSizeAndTime>();
            for (int i = 0; i < files.Count; i++)
            {
                string path = files[i].Path;
                try
                {
                    FileInfo info = new FileInfo(path);
                    stamps[path] = new FileSizeAndTime(info.Length, info.LastWriteTimeUtc.Ticks);
                }
                catch (IOException)
                {
                    // No stamp means "settled" to the pass. A file we cannot stat but could hash is
                    // not worth holding back.
                }
            }

            int queued = _pass.Run(
                mapping, files, stamps, Array.Empty<RemoteFileState>(), _runner, now.Ticks);

            if (queued > 0)
            {
                Console.WriteLine($"[sync] {queued} transfer(s) queued, {_runner.Queued} waiting");
            }
        }
    }
}
