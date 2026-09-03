using System;
using System.IO;

using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg;               // XmsgFrame
using RetroIO.Protocols.Xmsg.Servers.Fa;
using RetroIO.Protocols.Xmsg.Sync;

using RetroIO.Protocols.Xmsg.Live;
namespace RetroXmsg.Runner
{
    /// <summary>
    /// Carries the sync daemon's transfers over a real link, using the same file-access drivers
    /// that the one-shot <c>--push</c> and <c>--pull</c> use.
    /// </summary>
    /// <remarks>
    /// <para><b>Where the wire finally meets the daemon</b></para>
    /// <para>
    /// Everything in <c>RetroIO.Protocols.Xmsg.Sync</c> is deliberately free of the node, the transport and the
    /// peer, so its decisions can be tested without a machine. This class is the one place that
    /// knows about all three, and it exists so that boundary can hold.
    /// </para>
    /// <para><b>It reuses the proven runs rather than driving the drivers itself</b></para>
    /// <para>
    /// <see cref="FaPushRun"/> and <see cref="FaPullRun"/> already carry the hard-won details - not
    /// starting before the XMSG layer can address the peer, never letting a failed transfer take
    /// the node down with it, reporting once. Re-implementing that here would mean re-learning it.
    /// A transfer is therefore one short-lived run object, made when the transfer starts and
    /// dropped when it ends.
    /// </para>
    /// <para><b>The quoting rule is applied HERE, and it is not cosmetic</b></para>
    /// <para>
    /// A SINTRAN filespec is quoted when the file is being CREATED and bare when it already
    /// exists. The planner has already decided which case this is, so the decision only has to be
    /// honoured: a create gets quotes, an overwrite does not.
    /// </para>
    /// </remarks>
    internal sealed class LiveSyncTransferAgent : ISyncTransferAgent
    {
        private readonly XmsgNodeHost _nodeHost;
        private readonly ushort _serverNode;
        private readonly string _serverName;
        private readonly Func<bool> _linkReady;

        /// <summary>
        /// The folded password per user, so a push can enter a directory that is not ours.
        /// </summary>
        /// <remarks>
        /// A user missing from this list is pushed to with NO password, which is right for the
        /// passwordless users on these machines and produces an honest WRONG PASSWORD refusal for
        /// anything else - rather than a silent landing in the wrong directory.
        /// </remarks>
        private readonly SyncCredentials _credentials;

        private FaPushRun? _push;
        private FaPullRun? _pull;
        private string _localPath = string.Empty;
        private bool _pushing;

        /// <summary>
        /// Creates the agent.
        /// </summary>
        /// <param name="nodeHost">
        /// The node that supplies the transport and stamps the datagram fields.
        /// </param>
        /// <param name="serverNode">
        /// The machine's node number.
        /// </param>
        /// <param name="serverName">
        /// The machine's name, as the connect letter must address it.
        /// </param>
        /// <param name="linkReady">
        /// Answers whether the link can carry a frame we originate.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="nodeHost"/>, <paramref name="serverName"/> or
        /// <paramref name="linkReady"/> is null.
        /// </exception>
        public LiveSyncTransferAgent(
            XmsgNodeHost nodeHost, ushort serverNode, string serverName, Func<bool> linkReady,
            SyncCredentials credentials)
        {
            _nodeHost = nodeHost ?? throw new ArgumentNullException(nameof(nodeHost));
            _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
            _linkReady = linkReady ?? throw new ArgumentNullException(nameof(linkReady));
            _credentials = credentials;
            _serverNode = serverNode;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Not ready until the XMSG layer can actually address the peer. The link knowing the peer
        /// is NOT enough: the layer learns its seed when the first datagram is dispatched, and a
        /// frame built in that gap cannot be addressed at all.
        /// </remarks>
        public bool Ready
        {
            get
            {
                if (_push != null || _pull != null || !_linkReady())
                {
                    return false;
                }

                // A peer we have met before can be addressed straight away from its remembered
                // seed. Without this the daemon waits for the machine to speak first, which means
                // waiting for a person to type a command on it.
                //
                // MEASURED 2026-08-17 (morning), and RETRACTED the same day: D100 appeared to
                // REFUSE a letter sent from the remembered seed alone, answering XRUNN, "unknown
                // name of server or system". That reading was wrong. Every run behind it had
                // --resync-hard zeroing our outgoing Flags1 against a peer that keeps its own, so
                // the letter was behind-sequence before its name was ever read. Same root cause as
                // the "second process is refused" story.
                //
                // RE-MEASURED with the counter carried on from the store: D100 ACCEPTS a letter
                // sent from the remembered seed, and the daemon created a file with nobody at the
                // console (FILE 102 : (PACK-ONE:SYSTEM)PROOF:TXT;1). Nothing inbound arrived first
                // - the peer's frames turn up 164 ms AFTER we originate, answering us.
                //
                // The caller still chooses. The runner passes ServerHost.CanReach by default -
                // true only once an INBOUND datagram has arrived - and passes this weaker,
                // seed-based gate only under --originate-from-seed, because the cold start is
                // proved on D100 alone and D103 differs on the related reset question. What must
                // NOT be passed is a gate that is weaker still, such as "the LAPB link is up":
                // that says nothing about whether the peer can be addressed at all.
                return _nodeHost.ServerHost.OpenLinkFromRememberedSeed(_serverNode);
            }
        }

        /// <summary>
        /// Drops whatever transfer was running after it threw.
        /// </summary>
        /// <remarks>
        /// Without this the agent would report "a push is still running" for ever and the queue
        /// would never move again - a stall that looks like a dead link rather than a dead
        /// transfer.
        /// </remarks>
        public void AbandonTransfer()
        {
            _push = null;
            _pull = null;
        }

        /// <summary>
        /// Says which of the readiness conditions is not met, for the log.
        /// </summary>
        /// <returns>
        /// The reason, or a note that it IS ready.
        /// </returns>
        /// <remarks>
        /// <see cref="Ready"/> is three conditions and-ed together, so a false answer on its own
        /// sends you looking in three places. Naming the one that failed is the difference between
        /// a minute and an afternoon - the same lesson as the node's unconnected logs.
        /// </remarks>
        public string WhyNotReady()
        {
            if (_push != null) { return "a push is still running"; }
            if (_pull != null) { return "a pull is still running"; }
            if (!_linkReady()) { return "the link has not learned the peer yet"; }
            if (!_nodeHost.ServerHost.CanReach(_serverNode))
            {
                return $"node {_serverNode} has never sent us a datagram, so its envelope seed is"
                    + " unknown and cannot be guessed - it must talk to us once, ever";
            }

            return "it IS ready - if work is still waiting, that is a different fault";
        }

        /// <inheritdoc />
        public bool Begin(SyncTransferRequest request)
        {
            if (request == null) { throw new ArgumentNullException(nameof(request)); }
            if (_push != null || _pull != null) { return false; }

            _localPath = request.LocalPath;
            _pushing = request.IsPush;

            if (request.IsPush)
            {
                byte[] content;
                try
                {
                    content = File.ReadAllBytes(request.LocalPath);
                }
                catch (IOException)
                {
                    // Something still has it open. Refusing the START leaves the ledger untouched,
                    // so the next pass picks it up - which is what should happen.
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }

                // The plan names the file the way a person would type it at a SINTRAN terminal -
                // D100(SYSTEM)."NAME:TYPE". The OPEN request carries only the bare name, because
                // the conversation is already addressed to that machine. MEASURED 2026-08-11:
                // passing the addressed form through was refused as "27 characters" before it
                // ever left.
                string wireName = SyncFolderMap.ToWireName(request.FileSpec);

                // Quoted to CREATE, bare to replace. The planner already worked out which.
                string spec = request.Kind == SyncActionKind.Create
                    ? "\"" + wireName + "\""
                    : wireName;

                // AND THE USER HAS TO BE PUT BACK. Stripping it above only moves it: the machine
                // learns who is asking from the ReserveFileEntry request, not from the open. This
                // line was missing until 2026-08-24, so FaWriteTarget kept its "SYSTEM" default
                // and EVERY push landed in SYSTEM no matter what the mapping said. The daemon
                // printed "create D100(UTILITY)." and the file arrived as (SYSTEM) - the log and
                // the disk disagreed, and the log was the one being believed.
                FaWriteTarget target = new FaWriteTarget(_serverNode, _serverName, spec);
                string user = SyncFolderMap.ToUser(request.FileSpec);
                if (user.Length > 0)
                {
                    target.User = user;

                    // AND ITS PASSWORD. SINTRAN checks the password of the user whose directory
                    // is being opened, so a daemon serving somebody else has to present it or be
                    // refused. An unlisted user goes with no password, which is correct for the
                    // passwordless users here and gives an honest refusal for the rest.
                    ushort word;
                    if (_credentials.TryGet(user, out word))
                    {
                        target.PasswordWord = word;
                    }
                }

                _push = new FaPushRun(request.LocalPath, content, target);
                return true;
            }

            // A pull opens a file that already exists, so the name goes BARE - the quotes are
            // only for creating. Same rule as the one-shot --pull.
            FaReadSource source = new FaReadSource(
                _serverNode, _serverName, SyncFolderMap.ToWireName(request.FileSpec));

            // Same rule as the push above: the user rides in the reserve, not the open, so it has
            // to be set here or the read happens as whoever the session already is.
            string pullUser = SyncFolderMap.ToUser(request.FileSpec);
            if (pullUser.Length > 0)
            {
                source.User = pullUser;

                // Reading another user directory needs the password just as writing does.
                ushort pullWord;
                if (_credentials.TryGet(pullUser, out pullWord))
                {
                    source.PasswordWord = pullWord;
                }
            }

            _pull = new FaPullRun(request.LocalPath, source);
            return true;
        }

        /// <inheritdoc />
        public bool Poll(out SyncTransferResult? result)
        {
            result = null;

            if (_push != null)
            {
                if (!_push.Finished)
                {
                    return false;
                }

                // ASK THE TRANSFER HOW IT ENDED BEFORE LOOKING AT THE FILE.
                //
                // Outcome() judges a push by the LOCAL file - and the local file is the one we just
                // read in order to send it, so of course it is there and of course it hashes. That
                // says nothing whatever about whether the machine accepted it.
                //
                // MEASURED 2026-08-18: a push the machine REFUSED with SINTRAN error 39 was recorded
                // in the ledger as carried. A ledger entry means "this file is on the far machine",
                // so the file was then never sent again. Silent, permanent, and in the one part of
                // this program that runs unattended.
                //
                // Captured before the run is dropped, because the reason lives in the driver.
                string pushFailure = _push.Failed ? _push.Failure : string.Empty;
                int pushError = _push.SintranError;
                _push = null;

                if (pushFailure.Length > 0)
                {
                    // The NUMBER travels with the words. The runner decides on it - a create
                    // refused with 62 is the machine telling us the file is already there, which is
                    // the fact the planner was missing - and a decision made by matching on log
                    // text would break the first time somebody improved the text.
                    result = SyncTransferResult.Refused(pushFailure, pushError);
                    return true;
                }

                result = Outcome();
                return true;
            }

            if (_pull != null)
            {
                if (!_pull.Finished)
                {
                    return false;
                }

                // Same for the pull. A refused read does leave no file, so Outcome() would catch it
                // - but it would report "no file was written", which describes the symptom while
                // the machine already told us the cause.
                string pullFailure = _pull.Failed ? _pull.Failure : string.Empty;
                int pullError = _pull.SintranError;
                _pull = null;

                if (pullFailure.Length > 0)
                {
                    result = SyncTransferResult.Refused(pullFailure, pullError);
                    return true;
                }

                result = Outcome();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Feeds a received datagram to whichever transfer is running.
        /// </summary>
        /// <param name="frame">
        /// The frame the node received.
        /// </param>
        /// <remarks>
        /// Everything arriving on the node comes through here, most of it nothing to do with the
        /// transfer. The drivers decide what is theirs.
        /// </remarks>
        public void OnFrame(XmsgFrame frame)
        {
            if (frame == null) { return; }

            _push?.OnFrame(frame);
            _pull?.OnFrame(frame);
        }

        /// <summary>
        /// Sends whatever the running transfer wants to send next. Call once per loop tick.
        /// </summary>
        public void Pump()
        {
            bool ready = _linkReady();
            _push?.Pump(_nodeHost, ready);
            _pull?.Pump(_nodeHost, ready);
        }

        /// <summary>
        /// Works out how the finished transfer ended, by looking at the FILE rather than at what
        /// the driver believes.
        /// </summary>
        /// <returns>
        /// The outcome.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The hash comes from re-reading what is on disk. For a push that is the content that was
        /// sent; for a pull it is what actually landed. Either way it is measured, not assumed -
        /// and the ledger is only as trustworthy as this value, because a wrong hash here means a
        /// file silently never carried again.
        /// </para>
        /// <para>
        /// A pull that produced no file is a FAILURE even if the driver finished quietly. That is
        /// the case a "finished means succeeded" reading would record as done.
        /// </para>
        /// </remarks>
        private SyncTransferResult Outcome()
        {
            if (!_pushing && !File.Exists(_localPath))
            {
                return SyncTransferResult.Failed(
                    "the transfer ended but no file was written to " + _localPath);
            }

            byte[]? hash = LocalFolderScanner.TryHash(_localPath);
            if (hash == null)
            {
                return SyncTransferResult.Failed(
                    "could not read " + _localPath + " back to record what was carried");
            }

            long length;
            try
            {
                length = new FileInfo(_localPath).Length;
            }
            catch (IOException)
            {
                length = 0;
            }

            return SyncTransferResult.Ok(hash, length);
        }
    }
}
