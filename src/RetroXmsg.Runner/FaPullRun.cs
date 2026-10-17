//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroXmsg - Accessing yesterday's Xmsg API with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Protocols.Xmsg;               // XmsgFrame
using RetroIO.Protocols.Xmsg.Fa;   // FaClientAction
using RetroIO.Protocols.Xmsg.Live;
using RetroIO.Protocols.Xmsg.Node.Seam;     // XmsgNodeHost
using RetroIO.Protocols.Xmsg.Servers.Fa;    // FaReadDriver, FaReadSource
namespace RetroXmsg.Runner
{
    /// <summary>
    /// Drives one file pull from a remote <c>*FA-SERVER</c> into a local file, and says out loud
    /// what it is doing.
    /// </summary>
    /// <remarks>
    /// <para><b>The mirror of <see cref="FaPushRun"/></b></para>
    /// <para>
    /// Same shape, same guards, same reasons for them - a transfer must never take the node down,
    /// it cannot start before the link can carry a frame we originate, and it paces itself against
    /// the ladder rather than a timer. The differences are that the file arrives instead of
    /// leaving, and that its length is not known until the server's open reply.
    /// </para>
    /// <para><b>The local file is written ONCE, at the end</b></para>
    /// <para>
    /// Not block by block as the content arrives. A partly written file on disk looks exactly like
    /// a complete one to whatever reads it next, and a pull that dies halfway would leave a
    /// truncated file with a plausible name. Writing at the end means a file exists only when the
    /// whole thing arrived.
    /// </para>
    /// </remarks>
    internal sealed class FaPullRun
    {
        private readonly FaReadDriver _driver;
        private readonly string _localPath;
        private readonly string _fileSpec;
        private readonly ushort _serverNode;

        /// <summary>
        /// Whether the pull may start from the REMEMBERED seed instead of waiting to be addressed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is <c>--originate-from-seed</c>, and until 2026-08-17 only the SYNC daemon honoured
        /// it. The pull gated on <c>CanReach</c> alone, which is true only once the PEER has
        /// addressed us - so on a link where nothing arrives first the pull sat silent for ever. It
        /// did not even print the "reading" line that names the file, because that line comes
        /// after the gate, and a run that never starts looks exactly like a run that started and
        /// got no answer.
        /// </para>
        /// <para>
        /// Measured the same day: a push over the same seam, in the same minute, carried 42368
        /// bytes - because it went through the sync daemon, which built its readiness with the
        /// seed. The one-shot pull never sent a frame.
        /// </para>
        /// </remarks>
        public bool OriginateFromSeed { get; set; }
        /// <summary>
        /// Gets the node this transfer talks to.
        /// </summary>
        /// <remarks>
        /// Exposed so the runner can open the XMSG link from the REMEMBERED seed before pumping.
        /// Without that, a transfer can only start against a peer that has spoken to us first,
        /// which is useless for a daemon and makes a transfer against an idle peer unobservable.
        /// </remarks>
        public ushort RemoteNode
        {
            get { return _serverNode; }
        }


        private bool _started;
        private bool _reported;

        /// <summary>
        /// Set when the pull threw, so it stops instead of throwing again every tick.
        /// </summary>
        private bool _crashed;

        /// <summary>
        /// Set once the block count is known, so it is announced exactly once.
        /// </summary>
        private bool _announcedLength;

        // THE CONNECT-LETTER RETRY, PORTED FROM FaPushRun ON 2026-08-21.
        //
        // The push grew this on 2026-08-17 and the pull never got it, so for four days a pull whose
        // first connect letter went unanswered sat in total silence until the 240-second transfer
        // timeout and then said only "did NOT finish within 240s" - which names nothing. That one
        // uninformative line sent a whole day into suspecting XMSG, the PLANC compile, the restart
        // script, the chat programs and the machine itself. The answer was in our own frame log.
        //
        // MEASURED, xmsg-runner-seam.log 2026-08-21 18:09:43:
        //
        //   .404 [TX] connect letter -> *FA-SERVER, Flags1 0x034B
        //   .404 [pull] SendConnectLetter: 1 frame(s) sent
        //   .616 [RX] 100->19999 sub=ReachabilityRequest f1=0x034B
        //   .618 [seq] node 100: RESET to 0x0000 (was 0x034C); in-memory link dropped
        //        ... 240 SECONDS OF NOTHING ...
        //
        // D100 answered with a ReachabilityRequest - "I have restarted" - which correctly zeroes our
        // outgoing count. The letter sent under the OLD count is then orphaned: the peer waits for
        // us to ask again, we wait for an answer that can never come. But the reset is only ONE
        // reason a first letter goes unanswered; the real defect is that the pull sent it ONCE and
        // never again, so ANY such reason became a four-minute hang.
        //
        // It also explains "the first transfer after a restart is always refused, retry works",
        // which had been written up as harmless weather. The manual retry was doing by hand exactly
        // what this does.
        //
        // Only the CONNECT letter is retried. Once the peer has answered anything the ladder is
        // live, and a loss there is a different problem with different evidence.
        private byte[][]? _connectFrames;
        private DateTime _connectSentUtc;
        private int _connectAttempts;
        private bool _peerAnswered;

        // The transport's refusal count when the connect letter was FIRST handed over. Subtracting
        // it later separates "the peer ignored us" from "we never actually asked" - a frame our own
        // transport would not take is OUR fault and must not be reported as the peer's silence.
        private long _refusedAtConnectStart;

        // Long enough that a busy peer is not hurried, short enough that four attempts still fit
        // inside a transfer timeout. Same values as the push - they are proven there.
        private static readonly TimeSpan ConnectRetryAfter = TimeSpan.FromSeconds(5);
        private const int MaxConnectAttempts = 4;

        /// <summary>
        /// Creates a pull.
        /// </summary>
        /// <param name="localPath">
        /// Where to write the file once it has all arrived.
        /// </param>
        /// <param name="source">
        /// Where the file is coming from.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is null.
        /// </exception>
        public FaPullRun(string localPath, FaReadSource source)
            : this(localPath, source, false)
        {
        }

        /// <summary>
        /// Starts a pull, or a diagnostic probe that opens nothing.
        /// </summary>
        /// <param name="localPath">
        /// Where the file lands here. Unused by a probe, which reads no content.
        /// </param>
        /// <param name="source">
        /// Where the file is coming from.
        /// </param>
        /// <param name="probeWithoutOpen">
        /// <c>true</c> to reserve a file entry and set the block size without opening the file.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="localPath"/> or <paramref name="source"/> is null.
        /// </exception>
        /// <remarks>
        /// The probe answers what the follow-on refusal <c>A2 4104</c> means - see
        /// <see cref="FaReadDriver"/> and <c>DOC/CARVE-FA-READ-REFUSAL-2026-08-18.md</c>. It must be
        /// pointed at a file that EXISTS, or the ordinary "no such file name" refusal answers first
        /// and the run measures nothing.
        /// </remarks>
        public FaPullRun(string localPath, FaReadSource source, bool probeWithoutOpen)
        {
            if (localPath == null) { throw new ArgumentNullException(nameof(localPath)); }
            if (source == null) { throw new ArgumentNullException(nameof(source)); }

            _localPath = localPath;
            _serverNode = source.ServerNode;
            _fileSpec = source.FileSpec;
            _driver = new FaReadDriver(source, probeWithoutOpen);
        }

        /// <summary>
        /// Gets whether the pull has finished, either way.
        /// </summary>
        public bool Finished
        {
            get
            {
                // Not finished while a goodbye is owed - see FaPushRun for the measurement.
                if (_driver.ReleasePending) { return false; }

                return _crashed || _driver.Done || _driver.Failure.Length > 0;
            }
        }

        /// <summary>
        /// Gets whether the transfer finished BADLY, as opposed to merely finishing.
        /// </summary>
        /// <remarks>
        /// <c>Finished</c> is true either way - it answers "is it over", not "did it work" - and
        /// reading it as success is how a refused transfer came to exit 0. MEASURED 2026-08-18: a
        /// push the machine refused with SINTRAN error 39 printed a byte count and returned success.
        /// </remarks>
        public bool Failed
        {
            get { return _crashed || _driver.Failure.Length > 0; }
        }

        /// <summary>
        /// Gets why the transfer failed, or an empty string when it did not.
        /// </summary>
        /// <remarks>
        /// Carries the SERVER's own words where there are any - a refusal decodes to a SINTRAN
        /// error number and its meaning. Worth passing on rather than replacing with a summary:
        /// "No such user name in main directory" tells the operator what to fix, "the push failed"
        /// does not.
        /// </remarks>
        public string Failure
        {
            get { return _driver.Failure; }
        }

        /// <summary>
        /// Gets the SINTRAN error number behind the failure, or zero when there is none.
        /// </summary>
        /// <remarks>
        /// Taken from the driver rather than scraped back out of <see cref="Failure"/>. The text is
        /// for a person; a caller that has to make a DECISION on the number needs the number, and
        /// one caller does - the sync daemon treats 62, "File already exists", as an answer rather
        /// than a fault.
        /// </remarks>
        public int SintranError
        {
            get { return _driver.SintranError; }
        }

        /// <summary>
        /// Feeds a received datagram to the pull.
        /// </summary>
        /// <param name="frame">
        /// The frame the node received.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frame"/> is null.
        /// </exception>
        /// <remarks>
        /// Everything arriving on the node goes through here, including traffic that has nothing to
        /// do with the pull. The driver decides what is its own.
        /// </remarks>
        public void OnFrame(XmsgFrame frame)
        {
            if (frame == null) { throw new ArgumentNullException(nameof(frame)); }

            if (Finished)
            {
                return;
            }

            // "ANSWERED US", NOT "THE LINK IS BUSY". Every frame on the node comes through here,
            // including the peer's own requests to OUR file server, which arrive on the same link a
            // moment later. Setting the flag on any of them would silently disable the retry - the
            // exact failure it exists to cure, reintroduced one layer up. The push learned this the
            // hard way; the port keeps both guards.
            //
            //   _connectFrames != null   the letter has actually been sent
            //   DestinationPort == OurPort   the answer belongs to THIS conversation
            if (_connectFrames != null
                && frame.SubHeader != null
                && frame.SubHeader.DestinationPort == _driver.OurPort)
            {
                _peerAnswered = true;
            }

            _driver.OnFrame(frame);
        }

        /// <summary>
        /// Sends whatever the pull wants to send next.
        /// </summary>
        /// <param name="host">
        /// The node, which supplies the transport and stamps Flags 1, the counter and the channel.
        /// </param>
        /// <param name="linkReady">
        /// <see langword="true"/> once the link can carry a frame we originate.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="host"/> is null.
        /// </exception>
        public void Pump(XmsgNodeHost host, bool linkReady)
        {
            if (host == null) { throw new ArgumentNullException(nameof(host)); }

            if (ReportIfFinished())
            {
                return;
            }

            if (!linkReady)
            {
                return;
            }

            // The LINK knowing the peer is not enough. The XMSG layer learns its seed when the
            // first datagram is DISPATCHED, a tick after the link learned the peer's id, and a
            // frame built in that gap cannot be addressed at all.
            //
            // CanReach alone means "the peer has addressed us", which never happens on a link
            // where we speak first - so with it as the only gate a pull waits for ever and says
            // nothing while it waits. OriginateFromSeed opens the link from the REMEMBERED seed
            // instead, which is the same choice the sync daemon has always had.
            bool reachable = OriginateFromSeed
                ? host.ServerHost.OpenLinkFromRememberedSeed(_serverNode)
                : host.ServerHost.CanReach(_serverNode);

            if (!reachable)
            {
                return;
            }

            if (!_started)
            {
                _started = true;
                Console.WriteLine(
                    $"[pull] reading {_fileSpec} from node {_serverNode} into {_localPath}");
            }

            // The length arrives in the open reply, several steps in. Announced once, because it
            // is the number that decides everything after it - how many blocks are asked for and
            // where the file is trimmed.
            if (!_announcedLength && _driver.FileLength > 0)
            {
                _announcedLength = true;
                Console.WriteLine(
                    $"[pull] the server says {_fileSpec} is {_driver.FileLength} bytes, " +
                    $"so {_driver.BlockCount} block(s)");
            }

            // What the driver is ABOUT to do, read before building, because building advances the
            // ladder and the operation would then be the next one.
            // BEFORE THE Wait RETURN, DELIBERATELY. A pull whose connect letter went unanswered has
            // NOTHING to do - NextAction says Wait on every tick - so a retry placed after that
            // return would never run in the one case it exists for.
            RetryConnectLetterIfSilent(host);

            FaClientAction action = _driver.NextAction();
            if (action == FaClientAction.Wait)
            {
                return;
            }

            // Name the OPERATION for a request, not just "SendRequest" - a ladder that stalls is
            // diagnosed by which step it stalled on, and every step is a SendRequest.
            string describedOperation = action == FaClientAction.SendRequest
                ? _driver.CurrentOperation.ToString()
                : action.ToString();

            // A FILE TRANSFER MUST NEVER TAKE THE NODE DOWN. This runs on the link's own loop tick,
            // so an exception escaping here would kill the pump and every session on it.
            try
            {
                IReadOnlyList<XmsgFrame> frames = _driver.BuildNext(host.ServerHost);
                if (frames.Count == 0)
                {
                    return;
                }

                byte[][] sent = new byte[frames.Count][];
                for (int i = 0; i < frames.Count; i++)
                {
                    byte[] bytes = frames[i].ToArray();
                    sent[i] = bytes;
                    host.Transport.Send(new ReadOnlySpan<byte>(bytes));
                }

                // KEEP THE EXACT BYTES OF THE CONNECT LETTER so it can be sent again. Kept, not
                // rebuilt: a retransmit must carry the SAME sequence, and rebuilding would advance
                // the ladder instead of repeating the step.
                if (!_peerAnswered && _connectFrames == null)
                {
                    _connectFrames = sent;
                    _refusedAtConnectStart = host.Transport.RefusedFrames;
                    _connectSentUtc = DateTime.UtcNow;
                    _connectAttempts = 1;
                }

                // Every step logged as it goes out, the same as the push. Without this a stalled
                // ladder is invisible: the first live run showed "reading BIGPSH3:TXT" and then
                // nothing at all, and there was no way to tell a letter that was never sent from
                // one that was never answered.
                Console.WriteLine($"[pull] {describedOperation}: {frames.Count} frame(s) sent");
            }
            catch (Exception ex)
            {
                _crashed = true;

                // Marked reported here as well, or the next tick would see Finished with an empty
                // Failure and cheerfully announce that the file had arrived.
                _reported = true;
                Console.WriteLine(
                    $"[pull] *** FAILED *** {describedOperation} threw: {ex.Message}");
                Console.WriteLine("[pull] the node is still running; only the pull has stopped.");
                return;
            }

            ReportIfFinished();
        }

        /// <summary>
        /// Prints the outcome once, the first time the pull is over, and saves the file.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when the pull is finished.
        /// </returns>
        /// <summary>
        /// Sends the connect letter again when the peer has answered nothing at all.
        /// </summary>
        /// <param name="host">
        /// The node whose transport carries the frames.
        /// </param>
        /// <remarks>
        /// <para>
        /// Does nothing once the peer has answered, once the attempts are spent, or before the wait
        /// has elapsed. It gives up out loud rather than retrying for ever: a stall that says why is
        /// diagnosable, a flood is not.
        /// </para>
        /// <para>
        /// Ported from <c>FaPushRun</c>, where it has been proven since 2026-08-17.
        /// </para>
        /// </remarks>
        private void RetryConnectLetterIfSilent(XmsgNodeHost host)
        {
            if (_peerAnswered || _connectFrames == null)
            {
                return;
            }

            if (DateTime.UtcNow - _connectSentUtc < ConnectRetryAfter)
            {
                return;
            }

            if (_connectAttempts >= MaxConnectAttempts)
            {
                // SAY WHAT IS KNOWN, NOT WHAT IS ASSUMED. The transport counts the frames it would
                // not take, so ask it rather than asserting the link was fine. A frame our own
                // transport refused is OUR problem and must never be reported as the peer's
                // silence - the push's copy of this message got that wrong twice on live runs.
                long refused = host.Transport.RefusedFrames - _refusedAtConnectStart;

                if (refused > 0)
                {
                    Console.WriteLine(
                        $"[pull] GIVING UP: {refused} frame(s) were REFUSED BY OUR OWN TRANSPORT"
                        + " while trying to send the connect letter, so the file server was never"
                        + " asked. This is our end, not the peer's - the usual cause is sending"
                        + " before the link is up.");
                }
                else
                {
                    Console.WriteLine(
                        $"[pull] GIVING UP: the file server did not answer any of"
                        + $" {MaxConnectAttempts} connect letters. Our transport accepted every one,"
                        + " so they did go out. Check whether the FILE SERVER is running - an XMSG"
                        + " restart takes it down and it must be started by hand with FS-ADMINISTRATOR"
                        + " / SELECT-FSA / START-SERVER.");
                }

                _connectFrames = null;

                // SAY SO TO THE DRIVER, not just to the screen, or the transfer reports itself
                // merely unfinished and nothing above can tell a decided failure from one still in
                // progress. That is what turned a known answer into a four-minute wait.
                _driver.Abandon(refused > 0
                    ? $"our own transport refused {refused} connect frame(s), so the file server"
                      + " was never asked - send before the link was up"
                    : $"the file server did not answer any of {MaxConnectAttempts} connect letters"
                      + " (all were accepted by our transport) - check it is running");
                return;
            }

            _connectAttempts++;
            _connectSentUtc = DateTime.UtcNow;

            for (int i = 0; i < _connectFrames.Length; i++)
            {
                host.Transport.Send(new ReadOnlySpan<byte>(_connectFrames[i]));
            }

            Console.WriteLine(
                "[pull] no answer to the connect letter - sending it again"
                + $" (attempt {_connectAttempts} of {MaxConnectAttempts})");
        }

        private bool ReportIfFinished()
        {
            if (!Finished)
            {
                return false;
            }

            if (_reported)
            {
                return true;
            }

            _reported = true;

            if (_driver.Failure.Length > 0)
            {
                Console.WriteLine($"[pull] *** FAILED *** {_driver.Failure}");
                return true;
            }

            byte[] content = _driver.Content();

            try
            {
                System.IO.File.WriteAllBytes(_localPath, content);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[pull] read {content.Length} bytes but could not write '{_localPath}': "
                    + ex.Message);
                return true;
            }

            Console.WriteLine(
                $"[pull] finished: {content.Length} bytes read from {_fileSpec} into {_localPath}");

            // A non-zero count means the server repeated content and we discarded it as a repeat.
            // That is normal after a lost acknowledgement, but it is also the only way a byte could
            // go missing without anything else noticing, so it is said out loud rather than buried.
            Console.WriteLine(
                "[pull] compare it against the original before trusting it - our own log is not "
                + "evidence that the bytes are right.");

            return true;
        }
    }
}
