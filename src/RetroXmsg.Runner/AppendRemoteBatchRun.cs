using System;

using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Services;  // XmsgAnsweredFlags1
using RetroIO.Protocols.Xmsg;              // XmsgFrame, XmsgFrameFlags, XmsgSendOptions

using RetroIO.Protocols.Xmsg.Live;
namespace RetroXmsg.Runner
{
    /// <summary>
    /// Sends ONE <c>APPEND-REMOTE-BATCH</c> letter to a real machine and reports what comes back.
    /// </summary>
    /// <remarks>
    /// <para><b>What this is for</b></para>
    /// <para>
    /// The request bytes have been right for a while - <c>XftraRequests.AppendRemoteBatch</c> is
    /// built against a live capture and a test compares it to those bytes byte for byte. What had
    /// never happened is one of ours actually going out of a socket to a machine that answers. A
    /// builder that matches a capture proves the shape; only sending one proves the machine
    /// accepts it from US.
    /// </para>
    /// <para><b>Where it goes</b></para>
    /// <para>
    /// To XROUT's well-known port 0 as an XSLET letter, exactly like the file-access connect
    /// letter - <c>*XFTRA</c> is a name XROUT looks up, not an address we know. The frame flags and
    /// role are the same ones the file push uses, because both are letters asking a named server
    /// to do something.
    /// </para>
    /// <para><b>What a good answer looks like</b></para>
    /// <para>
    /// Not necessarily success. The captured exchange between two real machines ended in
    /// <c>NO SUCH FILE NAME</c> from the far side, and that was a VALID result - it proves the
    /// letter arrived, was understood, was routed to the batch handler and was acted on. A named
    /// refusal is a better outcome than silence, and silence is the failure to worry about.
    /// </para>
    /// </remarks>
    internal sealed class AppendRemoteBatchRun
    {
        private readonly ushort _serverNode;
        private readonly string _serverName;
        private readonly string _remoteUser;
        private readonly string _inputFile;
        private readonly string _outputFile;

        private bool _sent;
        private ushort _ourPort;

        /// <summary>
        /// Creates the run.
        /// </summary>
        /// <param name="serverNode">
        /// The node to ask.
        /// </param>
        /// <param name="serverName">
        /// That machine's name, which is what XROUT looks the server up under.
        /// </param>
        /// <param name="remoteUser">
        /// The user the batch runs as.
        /// </param>
        /// <param name="inputFile">
        /// The batch input file, as the REMOTE machine names it.
        /// </param>
        /// <param name="outputFile">
        /// The batch output file, as the remote machine names it.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any name is null.
        /// </exception>
        public AppendRemoteBatchRun(
            ushort serverNode, string serverName, string remoteUser, string inputFile, string outputFile)
        {
            _serverNode = serverNode;
            _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
            _remoteUser = remoteUser ?? throw new ArgumentNullException(nameof(remoteUser));
            _inputFile = inputFile ?? throw new ArgumentNullException(nameof(inputFile));
            _outputFile = outputFile ?? throw new ArgumentNullException(nameof(outputFile));
        }

        /// <summary>
        /// Gets whether the letter has gone out.
        /// </summary>
        public bool Finished
        {
            get { return _sent; }
        }

        /// <summary>
        /// Reports anything that arrives, so the answer is visible.
        /// </summary>
        /// <param name="frame">
        /// The frame the node received.
        /// </param>
        /// <remarks>
        /// Everything is printed rather than filtered. We do not know what an answer to this looks
        /// like arriving at US - the capture shows two SINTRAN machines, where the reply goes to a
        /// port that machine opened. Guessing which frames are "ours" would be the fastest way to
        /// hide the answer.
        /// </remarks>
        public void OnFrame(XmsgFrame frame)
        {
            if (frame == null || !_sent)
            {
                return;
            }

            if (frame.Header == null || frame.Header.SourceNode != _serverNode)
            {
                return;
            }

            Console.WriteLine(
                $"[arb] answer-ish frame from node {frame.Header.SourceNode}: "
                + $"subtype={frame.Header.Subtype} Flags1=0x{frame.Header.Flags1:X4} "
                + $"Flags2=0x{frame.Header.Flags2:X4}");
        }

        /// <summary>
        /// Sends the letter once, when the link can carry it.
        /// </summary>
        /// <param name="host">
        /// The node that stamps the datagram fields.
        /// </param>
        /// <param name="linkReady">
        /// Whether the link can carry a frame we originate.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="host"/> is null.
        /// </exception>
        public void Pump(XmsgNodeHost host, bool linkReady)
        {
            if (host == null) { throw new ArgumentNullException(nameof(host)); }

            if (_sent || !linkReady)
            {
                return;
            }

            // Same gate the file transfers use: the link knowing the peer is not enough, the XMSG
            // layer must be able to address it.
            if (!host.ServerHost.OpenLinkFromRememberedSeed(_serverNode))
            {
                return;
            }

            if (_ourPort == 0)
            {
                _ourPort = host.ServerHost.AllocateSessionPort();
            }

            // The serial is ours to choose and comes back echoed; the capture used 0x1B.
            XroutMessage message = XftraRequests.AppendRemoteBatch(
                serial: 0x1B,
                remoteSystem: _serverName,
                remoteUser: _remoteUser,
                batchInputFile: _inputFile,
                batchOutputFile: _outputFile,
                password: null);

            byte[] body = message.ToArray();

            Console.WriteLine(
                $"[arb] APPEND-REMOTE-BATCH to {_serverName}({_remoteUser}): input {_inputFile}, "
                + $"output {_outputFile}");
            Console.WriteLine($"[arb] letter body: {Convert.ToHexString(body)}");

            // A LETTER MUST NOT TAKE THE NODE DOWN - same rule as every other originated exchange.
            try
            {
                XmsgFrame frame = host.ServerHost.BuildBodyDatagram(
                    _serverNode,
                    _serverNode,
                    clientPort: 0x0000,                 // XROUT's well-known port
                    sourcePort: _ourPort,
                    xmcsm: (ushort)body.Length,
                    frameFlags: (byte)XmsgFrameFlags.Setup,
                    role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.WakeOnStatus
                        | XmsgSendOptions.HighPriority | XmsgSendOptions.RoutedLetter),
                    body: body,
                    answeredFlags1: XmsgAnsweredFlags1.None);

                byte[] bytes = frame.ToArray();
                host.Transport.Send(new ReadOnlySpan<byte>(bytes));

                _sent = true;
                Console.WriteLine("[arb] sent. Anything the machine says back is printed below.");
                Console.WriteLine(
                    "[arb] a NAMED refusal is a good result - it proves the letter arrived, was"
                    + " understood and was acted on. Silence is the bad one.");
            }
            catch (Exception ex)
            {
                _sent = true;
                Console.WriteLine($"[arb] *** FAILED to send: {ex.Message}");
            }
        }
    }
}
