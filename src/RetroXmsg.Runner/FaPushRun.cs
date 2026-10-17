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
using RetroIO.Protocols.Xmsg.Servers.Fa;    // FaWriteDriver, FaWriteTarget
namespace RetroXmsg.Runner
{
    /// <summary>
    /// Drives one file push from this runner to a remote <c>*FA-SERVER</c>, and says out loud what
    /// it is doing.
    /// </summary>
    /// <remarks>
    /// <para><b>The first thing we ORIGINATE rather than answer</b></para>
    /// <para>
    /// Everything the runner has done so far has been a reply: a machine asked, we answered. A push
    /// is the other way round, and that changes what can go wrong. The ladder is fifteen requests
    /// over seven operations, each one waiting for the server, so a single wrong field stalls
    /// rather than failing - which is why every step is logged as it is sent.
    /// </para>
    /// <para><b>It cannot start cold</b></para>
    /// <para>
    /// The link has to be up first. On the HDLC seam that means LAPB is
    /// <see cref="RetroIO.Protocols.Xmsg.Live.LapbLayerState.Connected"/>; the caller decides,
    /// because how "up" is known
    /// differs per transport - the Ethernet link cannot address a peer at all until the peer has
    /// addressed it.
    /// </para>
    /// </remarks>
    internal sealed class FaPushRun
    {
        private readonly FaWriteDriver _driver;
        private readonly string _localPath;
        private readonly string _fileSpec;
        private readonly int _contentLength;

        private readonly ushort _serverNode;
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
        /// Set when the push threw, so it stops instead of throwing again every tick.
        /// </summary>
        private bool _crashed;

        // THE CONNECT-LETTER RETRY. The letter is sent once, as soon as CanReach is true - and
        // CanReach is only "is there a link", which becomes true on ANY inbound frame, including
        // the InitializationNak the peer sends in answer to our announce. So the letter routinely
        // goes out before the peer is ready to answer it, the peer ignores it, and nothing is ever
        // sent again: a dead transfer with a healthy-looking log. MEASURED 2026-08-17, and it is
        // the single thing every client-side failure that day had in common.
        //
        // Only the CONNECT letter is retried. Once the peer has answered anything, the ladder is
        // live and a lost frame there is a different problem with different evidence.
        private byte[][]? _connectFrames;
        private DateTime _connectSentUtc;
        private int _connectAttempts;
        private bool _peerAnswered;

        // The transport's refusal count at the moment the connect letter was FIRST handed over.
        // Subtracting it later says how many of OUR OWN frames the transport would not take, which
        // is the difference between "the peer ignored us" and "we never asked". See the give-up
        // path for the two live occasions that distinction was got wrong.
        private long _refusedAtConnectStart;
        // _pumpTicks was declared here and never read; dropped so CS0169 does not fail the build.

        /// <summary>
        /// Gets or sets a value indicating whether the XMSG link may be opened from the REMEMBERED
        /// seed instead of waiting for the peer to address us first.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same defect that was found and fixed in the one-shot PULL, still present here.
        /// <c>CanReach</c> alone means "the peer has addressed us", which never becomes true on a
        /// seam where we are the side that speaks first. So <c>--originate-from-seed</c> was
        /// accepted on the command line, wired into the pull and into the sync daemon, and
        /// SILENTLY IGNORED by the one-shot push.
        /// </para>
        /// <para>
        /// MEASURED 2026-08-18 against a live D100: LAPB reached Connected, our announce went out,
        /// the peer answered, and XROUT letters arrived from node 100 for six minutes - while this
        /// gate never opened and not one FA frame was built. The log showed a healthy link and a
        /// transfer that had never begun, which is the worst shape a failure can take.
        /// </para>
        /// </remarks>
        public bool OriginateFromSeed { get; set; }

        /// <summary>
        /// How long to wait for the peer to answer the connect letter before sending it again.
        /// </summary>
        private static readonly TimeSpan ConnectRetryAfter = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How many connect letters to send in total before giving up.
        /// </summary>
        /// <remarks>
        /// Bounded, and it says so when it stops. An unbounded retry against a peer that is
        /// refusing for some OTHER reason turns one diagnosable stall into a flood that buries the
        /// evidence - measured elsewhere in this code as 127 rejects in sixteen seconds.
        /// </remarks>
        private const int MaxConnectAttempts = 4;

        /// <summary>
        /// Creates a push.
        /// </summary>
        /// <param name="localPath">
        /// The local file whose bytes are being sent, for the log.
        /// </param>
        /// <param name="content">
        /// The bytes to write.
        /// </param>
        /// <param name="target">
        /// Where they are going.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is null.
        /// </exception>
        public FaPushRun(string localPath, byte[] content, FaWriteTarget target)
        {
            if (localPath == null) { throw new ArgumentNullException(nameof(localPath)); }
            if (content == null) { throw new ArgumentNullException(nameof(content)); }
            if (target == null) { throw new ArgumentNullException(nameof(target)); }

            _localPath = localPath;
            _serverNode = target.ServerNode;
            _fileSpec = target.FileSpec;
            _contentLength = content.Length;
            _driver = new FaWriteDriver(target, content);
        }

        /// <summary>
        /// Gets whether the push has finished, either way.
        /// </summary>
        public bool Finished
        {
            get
            {
                // NOT finished while a goodbye is still owed. A refusal sets Failure at once, and
                // stopping there would cut the conversation off before its Release reached the
                // wire - leaving the server holding the connection seat, which is exactly what
                // sending one is for.
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
        /// Feeds a received datagram to the push.
        /// </summary>
        /// <param name="frame">
        /// The frame the node received.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frame"/> is null.
        /// </exception>
        /// <remarks>
        /// Everything arriving on the node goes through here, including traffic that has nothing to
        /// do with the push - a TAD session on the same link, for instance. The driver decides what
        /// is its own; failing the push on somebody else's frame would fail it for a reason that has
        /// nothing to do with it.
        /// </remarks>
        public void OnFrame(XmsgFrame frame)
        {
            if (frame == null) { throw new ArgumentNullException(nameof(frame)); }

            // Only a frame addressed to OUR port, after our connect letter, means it landed.
            //
            // This hook sees EVERY frame on the link, including the peer's own requests to our
            // file server, and an earlier cut set the flag on any of them. That silently disabled
            // the retry: the peer's unrelated traffic arrived first, the flag went up, and the
            // connect letter was then never re-sent - the exact failure this retry exists to fix,
            // reintroduced one layer up. Guarding on the letter having been sent is what makes the
            // flag mean "answered US" rather than "the link is busy".
            //
            // Guarding on the letter alone was still not enough: the peer's own requests to our
            // file server arrive on the SAME link a moment later and set the flag again. The port
            // check is what ties an answer to THIS conversation.
            if (_connectFrames != null
                && frame.SubHeader != null
                && frame.SubHeader.DestinationPort == _driver.OurPort)
            {
                _peerAnswered = true;
            }

            // A REFUSAL IS NOT SILENCE. SHOW IT, AND SHOW IT BEFORE ANY PORT TEST.
            //
            // The peer refuses a letter with a CONTROL datagram: XDTYP bit 0 (XD5CO) says control,
            // bit 2 (XD5BA) says BAD STATUS, and the reason rides in word 5 as a NEGATED XroutError.
            // Such a datagram is seven header words and NOTHING else - no sub-header, no port - so
            // every display below, gated on SubHeader and on our port, skipped it in silence.
            //
            // That is how "no answer to the connect letter" got printed three times in one log while
            // the machine was answering XRMFL, "Remote system message table space full". 39 of these
            // went past unread in a single session. The lesson one block down - that a driver which
            // discards replies cannot be debugged from its own log - was right and did not go far
            // enough: it was applied only to frames that HAVE a sub-header.
            if (frame.Header != null)
            {
                ushort datagramType = (ushort)((frame.Header.PacketType << 8) | (byte)frame.Header.Subtype);
                if (SintranControlStatus.TryGetRefusal(datagramType, frame.Header.Flags2, out XroutError refusal))
                {
                    Console.WriteLine(
                        "[push] <- node " + frame.Header.SourceNode + " REFUSED us: " + refusal
                        + " (" + (int)refusal + ")"
                        + " - XDTYP=0x" + datagramType.ToString("X4")
                        + " XDSCR=0x" + frame.Header.Flags2.ToString("X4"));
                }
            }

            // SHOW EVERY FRAME THE PEER SENDS TO OUR PORT, matched or not.
            //
            // The peer has been ANSWERING our connect letter all along - with XRUNN, "unknown name
            // of server or system" - and this driver dropped the answer because it decodes only the
            // replies it expects. The ladder then stalled with a healthy-looking log, and a great
            // deal of time went into explaining a silence that was never silent.
            //
            // A driver that discards replies cannot be debugged from its own log. This costs one
            // line per frame and would have ended that hunt in minutes.
            if (frame.SubHeader != null
                && frame.SubHeader.DestinationPort == _driver.OurPort
                && frame.Header != null
                && frame.Header.Subtype == SintranPacketSubtype.Data)
            {
                byte[] body = frame.GetBodyBytes();
                int show = body.Length < 16 ? body.Length : 16;
                Console.WriteLine(
                    "[push] <- from node " + frame.Header.SourceNode
                    + " on our port 0x" + _driver.OurPort.ToString("X4")
                    + ", " + body.Length + " byte(s): "
                    + Convert.ToHexString(body, 0, show)
                    + (body.Length > show ? "..." : string.Empty));
            }

            if (Finished)
            {
                return;
            }

            _driver.OnFrame(frame);
        }

        /// <summary>
        /// Sends whatever the push wants to send next.
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
        /// <remarks>
        /// Called once per loop tick. The driver returns nothing while it is waiting for the
        /// server, so this paces itself against the ladder rather than against a timer.
        /// </remarks>
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
            // first datagram is DISPATCHED, which is a tick after the link learned the peer's id,
            // and a frame built in that gap cannot be addressed at all. Measured the hard way: the
            // push fired in the gap and the exception took the whole runner down.
            // CanReach alone means "the peer has addressed us", which never happens on a seam where
            // we speak first - so with it as the only gate a push waits for ever and says nothing
            // while it waits. OriginateFromSeed opens the link from the REMEMBERED seed instead,
            // which is the same choice the pull and the sync daemon already had.
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
                    $"[push] sending {_localPath} ({_contentLength} bytes) to {_fileSpec} " +
                    $"in {_driver.BlockCount} block(s)");
            }

            // What the driver is ABOUT to do, read before building, because building advances the
            // ladder and the operation would then be the next one.
            FaClientAction action = _driver.NextAction();
            if (action == FaClientAction.Wait)
            {
                RetryConnectLetterIfSilent(host);
                return;
            }

            string describedOperation = action == FaClientAction.SendRequest
                ? _driver.CurrentOperation.ToString()
                : action.ToString();

            // A FILE TRANSFER MUST NEVER TAKE THE NODE DOWN. This runs on the link's own loop
            // tick, so an exception escaping here kills the pump and every session on it - which
            // is exactly what happened the first time this ran against a real machine. A push that
            // fails is a failed push; the node keeps serving.
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

                // Keep the CONNECT letter so it can be sent again if the peer never answers. The
                // exact bytes are kept, not rebuilt: a retransmit carries the SAME sequence, and
                // rebuilding would advance the ladder instead of repeating the step.
                if (!_peerAnswered && _connectFrames == null)
                {
                    _connectFrames = sent;
                    _refusedAtConnectStart = host.Transport.RefusedFrames;
                    _connectSentUtc = DateTime.UtcNow;
                    _connectAttempts = 1;
                }

                Console.WriteLine(
                    $"[push] {describedOperation}: {frames.Count} frame(s) sent");
            }
            catch (Exception ex)
            {
                _crashed = true;

                // Marked reported here as well, or the next tick would see Finished with an empty
                // Failure and cheerfully announce that the file had been written.
                _reported = true;
                Console.WriteLine(
                    $"[push] *** FAILED *** {describedOperation} threw: {ex.Message}");
                Console.WriteLine("[push] the node is still running; only the push has stopped.");
                return;
            }

            ReportIfFinished();
        }

        /// <summary>
        /// Prints the outcome once, the first time the push is over.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when the push is finished.
        /// </returns>
        /// <summary>
        /// Sends the connect letter again when the peer has answered nothing at all.
        /// </summary>
        /// <param name="host">
        /// The node whose transport carries the frames.
        /// </param>
        /// <remarks>
        /// Does nothing once the peer has answered, once the attempts are spent, or before the
        /// wait has elapsed. It gives up out loud rather than retrying forever: a stall that says
        /// why is diagnosable, a flood is not.
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
                // SAY WHAT IS KNOWN, NOT WHAT IS ASSUMED.
                //
                // This used to add "The link is up and the frame is well formed, so this is not a
                // lost frame". It had checked neither, and it was WRONG TWICE:
                //  - 2026-08-19: the peer DID answer. Our own LAPB rejected the reply with FRMR one
                //    layer below, and this message sent the search off into sequence numbers and
                //    routing tables for an evening.
                //  - 2026-08-20: the letters were never transmitted at all. The link was still
                //    establishing, SendData refused them, and "answered none" was true only
                //    because none had been sent.
                //
                // The transport counts refusals, so ask it instead of asserting. A frame the
                // transport would not take is OUR problem and must not be reported as the peer's
                // silence.
                long refused = host.Transport.RefusedFrames - _refusedAtConnectStart;

                if (refused > 0)
                {
                    Console.WriteLine(
                        $"[push] GIVING UP: {refused} frame(s) were REFUSED BY OUR OWN TRANSPORT"
                        + " while trying to send the connect letter, so node "
                        + $"{_serverNode} was never asked. This is our end, not the peer's - the"
                        + " usual cause is sending before the link is up.");
                }
                else
                {
                    Console.WriteLine(
                        $"[push] GIVING UP: node {_serverNode} did not answer any of"
                        + $" {MaxConnectAttempts} connect letters. Our transport accepted every"
                        + " one, so they did go out - but whether the peer saw them, or answered"
                        + " and we discarded the answer lower down, is NOT established here."
                        + " Check the log for FRMR before blaming the machine.");
                }

                _connectFrames = null;

                // SAY SO TO THE DRIVER, not just to the screen.
                //
                // Printing "GIVING UP" and returning left the transfer reporting itself unfinished,
                // so nothing above could tell a decided failure from one still in progress.
                // MEASURED 2026-08-18: a push to a user that does not exist gave up at 25 seconds
                // and the process then sat there until the wall-clock timeout at 45 - it had known
                // the answer for twenty seconds and had no way to say it.
                _driver.Abandon(refused > 0
                    ? $"our own transport refused {refused} connect frame(s), so node"
                      + $" {_serverNode} was never asked - send before the link was up"
                    : $"node {_serverNode} did not answer any of {MaxConnectAttempts} connect"
                      + " letters (all were accepted by our transport)");
                return;
            }

            _connectAttempts++;
            _connectSentUtc = DateTime.UtcNow;

            for (int i = 0; i < _connectFrames.Length; i++)
            {
                host.Transport.Send(new ReadOnlySpan<byte>(_connectFrames[i]));
            }

            Console.WriteLine(
                "[push] no answer to the connect letter - sending it again"
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

            if (_driver.Failure.Length > 0)
            {
                // A FAILED PUSH MUST STILL PUT THE FILE DOWN. From the moment OpenFile is
                // answered the peer holds the file open, and walking away leaves it open for
                // good: it cannot be rewritten, and it cannot even be deleted - SINTRAN answers
                // FILE ALREADY OPEN. It does not show in LIST-OPEN-FILES either, because the file
                // server's RT program owns it rather than any terminal, so the only way out is a
                // file-server restart. Measured on D100 2026-08-17 after a stalled push of
                // CHAT:PLNC.
                //
                // So the content is abandoned but the epilogue is not: SetEndOfFile, CloseFile,
                // ReleaseFileEntry go out exactly as a finished write sends them. The file ends
                // up short rather than absent, which the machine can recover from on its own.
                if (_driver.FileOpenOnPeer && _driver.AbandonButCloseFile())
                {
                    Console.WriteLine(
                        $"[push] *** FAILED *** {_driver.Failure}");
                    Console.WriteLine(
                        "[push] the file is open on the peer - sending the close anyway, "
                        + "because an abandoned write leaves it stuck until the file server "
                        + "is restarted.");
                    return false;
                }
            }

            _reported = true;

            if (_driver.Failure.Length > 0)
            {
                Console.WriteLine($"[push] *** FAILED *** {_driver.Failure}");
            }
            else
            {
                Console.WriteLine(
                    $"[push] finished: {_contentLength} bytes written to {_fileSpec}. " +
                    "Check it with LIST-FILES on the machine.");
            }

            return true;
        }
    }
}
