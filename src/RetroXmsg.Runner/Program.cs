// Live runner: connects the C# XMSG stack to a real nd100x --hdlc TCP bridge, brings up the
// LAPB link, answers reachability / list-route, and (with a TAD responder) accepts connect-to.
//
// Two composition paths:
//   * SEAM (default): TcpBridgeTransport -> LapbLayerAdapter(ILink) -> ProtocolDetector ->
//     XmsgCodec -> XmsgLayer. This is the restructured stack (XMSG-TRANSPORT-SEAM-PLAN.md).
//   * LEGACY (first arg == "legacy"): the original LiveNode + XmsgNode wiring, kept until the
//     seam path is proven live against machine 100 (Phase 5 gate).
//
// Usage:  RetroXmsg.Runner [legacy] [host] [port] [nodeDecimal] [seconds]
//   defaults: seam  127.0.0.1 10364 103 120

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Codec;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Live;          // LapbLayer, TcpBridgeTransport, LiveNode (replaceable half)
using RetroIO.Protocols.Xmsg.Live.Logging;  // RotatingFileWriter, TeeTextWriter, LogLevel
using RetroXmsg.Runner;   // TopologyConfig / TopologyNode / LogConfig (this project)
using RetroIO.Protocols.Xmsg.Live.Seam;     // LapbLayerAdapter (HDLC) + EthernetLink (COSMOS segment)
using RetroIO.Protocols.Xmsg.Ethernet;      // IEthernetBackend, EthernetBackendFactory, NdMacAddress
using RetroIO.Protocols.Xmsg.Ndfs;          // FolderFileStore (a Windows folder as an ND file store)
using RetroIO.Protocols.Xmsg.Servers.Fa;    // FaServer (*FA-SERVER, the COSMOS file server)
using RetroIO.Protocols.Xmsg.Servers.Tad;   // TadServer, TadUser, TadUserDirectory
using RetroIO.Protocols.Xmsg.Node;          // XmsgNode, FileResponderSequenceStore (portable half)
using RetroIO.Protocols.Xmsg.Node.Services; // IXmsgServer (the registered-server contract)
using RetroIO.Protocols.Xmsg.Node.Seam;     // ILink, XmsgLayer, LinkXmsgTransport, BoundProtocolDetector
using RetroIO.Protocols.Xmsg.Packet;

// Wraps a TextWriter so every emitted line begins with a full wall-clock timestamp
// (yyyy-MM-dd HH:mm:ss.fff) FOLLOWED BY " | " - the pipe is an explicit, machine-parseable
// delimiter so an LLM (or a script) can split "timestamp | message" on the first " | " with
// zero ambiguity. The date+seconds+ms let the frame log show exact send/receive ordering,
// essential for diagnosing LAPB N(S)/N(R) / retransmit timing.
internal sealed class TimestampWriter : System.IO.TextWriter
{
    private readonly System.IO.TextWriter _inner;
    private bool _atLineStart = true;

    // The delimiter that separates the timestamp from the message. First occurrence per line
    // marks where the date ends - split on " | " (with the surrounding spaces) to be safe even
    // if a message body itself contains a bare '|'.
    private const string Delimiter = " | ";

    public TimestampWriter(System.IO.TextWriter inner) { _inner = inner; }

    public override System.Text.Encoding Encoding { get { return _inner.Encoding; } }

    public override void Write(char value)
    {
        if (_atLineStart)
        {
            // Full date + time down to milliseconds, then the explicit delimiter.
            _inner.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            _inner.Write(Delimiter);
            _atLineStart = false;
        }

        _inner.Write(value);
        if (value == '\n')
        {
            _atLineStart = true;
        }
    }

    public override void Write(string? value)
    {
        if (value == null)
        {
            return;
        }

        for (int i = 0; i < value.Length; i++)
        {
            Write(value[i]);
        }
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Pull the named options (--config, --self) out of the vector before positional parsing so the
        // existing "[legacy] host port node seconds" positions are unchanged.
        List<string> argList = new List<string>(args);
        string? configPath = TakeOption(argList, "--config");
        string? selfArg = TakeOption(argList, "--self");

        // The relay options come out HERE too, with the others. Taking them later left them in the
        // vector during positional parsing, and "--relay-inbound-node" was read as the node number.
        string? relayListen = TakeOption(argList, "--relay-listen");
        string? relayInboundPeer = TakeOption(argList, "--relay-inbound-node");

        // PUSH: send a local file to a remote *FA-SERVER once the link is up. This is the first
        // thing the runner ORIGINATES rather than answers, so it is off unless asked for, and it
        // comes out of the vector here with the other options - leaving one behind meant
        // "--relay-inbound-node" was once read as the node number.
        // PER-FRAME TRACING IS OFF BY DEFAULT, and that is a correctness matter rather than a
        // tidiness one.
        //
        // MEASURED 2026-08-10. The segment sniffer and the transmit logger both sit on the hot
        // frame path and, for every frame addressed to us, wrote three lines including TWO full
        // hex dumps of the whole frame - synchronously, timestamped, tee'd to a rotating file.
        // Our acknowledgements then took a median of 1.77 s (p90 7.6 s, worst 8.6 s) while D100's
        // retransmit timer is 0.19 s. It resent every unacknowledged frame roughly nine times over
        // before our answer appeared, gave up, and dropped the LINK - which is the link flap that
        // was mistaken for a protocol fault for several rounds.
        //
        // Turn it on with --trace-frames when a capture is not available. Prefer a real capture.
        bool traceFrames = TakeFlag(argList, "--trace-frames");

        // Tell the peer our XMSG restarted, so it resets the datagram sequence it expects
        // from us. OFF by default and deliberately opt-in - see the long note where the
        // Ethernet announcer is built for what it is for and the one time it did harm.
        //
        // DO NOT REACH FOR THIS TO FIX A PEER THAT IS REFUSING YOU. It is what CAUSES that.
        //
        // MEASURED 2026-08-18 against a freshly brought-up D100. A push carrying this flag was
        // refused, and the peer said why:
        //
        //     [push] <- node 100 REFUSED us: XRDDF (3) - XDTYP=0x0017 XDSCR=0xFFFD
        //
        // XDTYP 0x0017 is an InitializationNak and XRDDF is "Another port already has this name".
        // The announce claims a name the peer already holds, the peer NAKs the initialise, and the
        // conversation is dead from there. Drop the flag and the IDENTICAL push completes - and a
        // second consecutive push completes after it, with no bring-up and no link cycle between.
        //
        // That also killed a rule we believed twice: "one client conversation per bring-up". There
        // is no such limit. It was this flag, in every command line, because it was in the recipe.
        bool announceRestart = TakeFlag(argList, "--announce-restart");

        // Zero OUR counter as well as announcing. Only meaningful with --announce-restart.
        //
        // REFUTED 2026-08-17 - do NOT reach for this to fix a peer that is ignoring us. Neither D103
        // (HDLC) nor D100 (seam) resets its expected-from-us on our announce, so zeroing ours puts
        // every later frame behind-sequence, where SINTRAN drops it without an error. That silence
        // is what made a second runner process look "refused" for a whole night. The counter in
        // xmsg-sequence.state is the correct value: carry on from it. To check it against the
        // machine rather than guessing, read X-C LIST-SYSTEMS on the ND - the "Sequence no.
        // Send-receive" columns publish the pair, and the receive column equals our next Flags1.
        bool resyncHard = TakeFlag(argList, "--resync-hard");

        // The folder-watch daemon: mirror a local folder onto a SINTRAN user directory, carrying
        // each file as it settles. --sync-user and --sync-to name the far end.
        string? syncFolder = TakeOption(argList, "--sync");

        // THE SHARED-SERVICE SHAPE. --sync-root names a folder whose SUBFOLDERS are SINTRAN user
        // names, and every one of them is served by this single daemon:
        //
        //     sync-root\SYSTEM\CHATSV.PLNC    ->  (SYSTEM)CHATSV:PLNC
        //     sync-root\UTILITY\XSTART.MODE   ->  (UTILITY)XSTART:MODE
        //
        // A SINTRAN user directory is FLAT, so a folder can only correspond to a user - which is
        // why the layout is one level of user folders and not an arbitrary tree. Passwords come
        // from sync-credentials.txt beside the executable.
        //
        // Only ONE runner may hold a given node number: the peer keeps a single sequence counter
        // per system, and two processes sharing the number leave one of them permanently behind,
        // whereupon the machine acks its frames at the link layer and answers nothing. That is
        // exactly what a shared service avoids - run this one, and let everybody drop files in.
        string? syncRoot = TakeOption(argList, "--sync-root");

        // Fetch requests ride the SAME held-open link. Without this the only way to get a file OFF
        // the machine was a second, one-shot runner, and a one-shot ends with DISC - a link
        // teardown, which is what kills the peer's XMSG after a transfer. It also could not run
        // while the daemon held the link at all.
        string? syncPullFolder = TakeOption(argList, "--sync-pull");

        // Send ONE APPEND-REMOTE-BATCH letter to *XFTRA and print whatever comes back. The request
        // bytes have matched a live capture for a while; what had never happened is one of ours
        // actually leaving a socket.
        string? batchInput = TakeOption(argList, "--append-batch");
        string? batchOutput = TakeOption(argList, "--append-batch-out");

        // Which machine the batch letter is addressed to. The Ethernet path takes this from the
        // configured peer; on a point-to-point bridge there is normally only one candidate, so this
        // defaults to the adjacent routing entry and only needs giving when that guess is wrong.
        string? batchToNode = TakeOption(argList, "--batch-to");

        // ---- THE LIVE CHAT LOAD RUN --------------------------------------------------------
        // N simulated users against a REAL room on a real machine. The unit tests in
        // RetroIO.Protocols.Xmsg.Chat.Tests already run twenty users and ten thousand messages, but in one process
        // with no wire - so they prove OUR room, not the machine's. This is the other half of the
        // question: does the PLANC CHATSV survive twenty arriving at once. See ChatLoadRun.
        //
        // CHAT-LOBBY has sixteen seats, so --chat-load 20 is ALSO the overload case: four should
        // be refused by XROUT and the sixteen inside should carry on.
        string? chatLoad = TakeOption(argList, "--chat-load");

        // --chat-room NAMES ROOMS NOW, not an XROUT name, and it takes a comma-separated list.
        // "--chat-room LOBBY,GENERAL" spreads the users over two rooms and turns the run into an
        // isolation test: see ChatLoadRun.ExpectedSaid, which says what a correct server must send.
        string? chatRoom = TakeOption(argList, "--chat-room");

        // The single registered name the server listens on. Only worth overriding when testing a
        // second server beside the real one.
        string? chatService = TakeOption(argList, "--chat-service");
        string? chatToNode = TakeOption(argList, "--chat-to");
        string? chatLines = TakeOption(argList, "--chat-lines");
        string? syncUser = TakeOption(argList, "--sync-user");

        // A root is the same daemon with a marker for the user, so everything downstream - the
        // readiness gate, the pull side, the ledger - is untouched.
        if (syncRoot != null)
        {
            syncFolder = syncRoot;
            syncUser = SyncDaemon.RootMarker;
        }
        string? syncToNode = TakeOption(argList, "--sync-to");

        // Ask the peer to open the ND link again, so it drops the sequence it remembers from
        // our last run. OFF by default: no ND has ever been seen to ANSWER a connection
        // request of ours, which is what turning this on is meant to find out.
        bool requestLink = TakeFlag(argList, "--request-link");

        // Let the sync daemon originate to a peer it has met before, from the seed remembered in
        // xmsg-link-seed.state, instead of waiting for that peer to address us again. This is the
        // difference between a daemon that runs from cold and one that needs somebody to type a
        // command on the far machine first. OFF by default and an EXPERIMENT - see
        // BuildSyncReadiness for the measurement that argues against it and why it is worth
        // repeating now that the sequence law is settled.
        bool originateFromSeed = TakeFlag(argList, "--originate-from-seed");

        // HOW LONG A ONE-SHOT --push OR --pull MAY TAKE BEFORE WE GIVE UP ON IT.
        //
        // A NET, NOT A DIAGNOSIS. It does not know why a transfer is stuck; it only refuses to sit
        // there for the default hour pretending to work.
        //
        // MEASURED 2026-08-18, and this is the case that needed it: a --pull for a file that does
        // not exist on D100 climbs the whole ladder, D100 answers, the driver acks that answer and
        // then waits for ever - because the driver has a Failure state and nothing sets it for a
        // server-side error. The run held the seam until the --for window expired.
        //
        // Sized against real transfers: a 107603-byte listing pulls in about 26 seconds wall clock
        // including link bring-up, and a 60355-byte push takes about 50. 240 leaves room for a file
        // several times larger. Raise it for a genuinely big transfer rather than lowering it to
        // make a hang fail faster - a cut-off transfer looks exactly like the fault it hides.
        int transferTimeout = int.Parse(TakeOption(argList, "--transfer-timeout") ?? "240");

        string? pushFile = TakeOption(argList, "--push");
        string? pushAs = TakeOption(argList, "--push-as");
        string? pushToNode = TakeOption(argList, "--push-to");

        // REPLACE what is already there, rather than create something new. Without this the name
        // is quoted, SINTRAN is asked to create, and a file that exists is refused with error 62.
        // Carrying a source file over a second time is the ordinary case for this tool, so the
        // flag is short and the refusal below says its name.
        bool pushOverwrite = TakeFlag(argList, "--push-overwrite");

        // The other direction. --pull names the file ON THE MACHINE and --pull-to where to put it
        // here; --pull-from picks the node, the same way --push-to does.
        string? pullSpec = TakeOption(argList, "--pull");
        string? pullTo = TakeOption(argList, "--pull-to");
        string? pullFromNode = TakeOption(argList, "--pull-from");

        // DIAGNOSTIC, not a transfer. Runs the pull ladder with the OpenFile step left out, to find
        // out what the server says about setting a block size on an entry nothing has opened. It
        // answers what the follow-on refusal A2 4104 means - see
        // DOC/CARVE-FA-READ-REFUSAL-2026-08-18.md. Point it at a file that EXISTS, or the ordinary
        // "no such file name" refusal answers first and the run measures nothing.
        bool faProbe = TakeFlag(argList, "--fa-probe-without-open");

        // Resolve and load the topology: --config wins, else the topology.json shipped next to the exe.
        System.IO.TextWriter realConsole = Console.Out;
        string resolvedConfigPath = configPath
            ?? System.IO.Path.Combine(AppContext.BaseDirectory, "topology.json");
        TopologyConfig? topology = TryLoadTopology(resolvedConfigPath, realConsole);

        // Set up logging: the console, optionally tee'd to a rotating file, all timestamped. The file is
        // rotated once here (fresh empty log per run) and again by size while running.
        System.IO.TextWriter sink = realConsole;
        RotatingFileWriter? fileWriter = TryCreateLogWriter(topology, realConsole);
        if (fileWriter != null)
        {
            sink = new TeeTextWriter(realConsole, fileWriter);
        }

        // Timestamp every line so the frame log shows exact ordering (LAPB timing). Both the console and
        // the file receive the same timestamped stream.
        Console.SetOut(new TimestampWriter(sink));

        // Startup banner: version + build time, and the full folders the config and log live in. This
        // lands in the log file too (it is printed after logging is wired), so a captured log records
        // exactly which build produced it and where its inputs/outputs are.
        PrintStartupBanner(resolvedConfigPath, fileWriter);

        // Leading "client" keyword runs the CONNECT-TO CLIENT (asker) instead of the responder:
        //   RetroXmsg.Runner client [host] [port] [ownNode] [targetName] [hostNode]
        // It brings up LAPB, sends a connect-to letter naming <targetName>, drives the TAD asker
        // state machine, renders the host's terminal output and sends what you type on stdin.
        if (argList.Count > 0 && string.Equals(argList[0], "client", StringComparison.OrdinalIgnoreCase))
        {
            return await RunClientModeAsync(argList, topology);
        }

        // Optional leading "legacy" keyword selects the old LiveNode + XmsgNode path.
        bool legacy = argList.Count > 0 && string.Equals(argList[0], "legacy", StringComparison.OrdinalIgnoreCase);
        int argOffset = legacy ? 1 : 0;

        // Effective self node: --self (id or alias) > positional node arg > topology.self > 103.
        ushort node = ResolveSelf(topology, selfArg, argList, argOffset);

        // Endpoint: positional host/port override the neighbour's TCP endpoint from the topology.
        TopologyEndpoint? endpoint = topology?.PrimaryEndpoint();
        string host = argList.Count > argOffset ? argList[argOffset] : (endpoint?.Host ?? "127.0.0.1");
        int port = argList.Count > argOffset + 1 ? int.Parse(argList[argOffset + 1]) : (endpoint?.Port ?? 10364);
        // Session duration in seconds. 0 (or negative) = run until the process is stopped (Ctrl-C),
        // so an interactive terminal session is not cut off by a timeout. Default 3600 (1 hour).
        int seconds = argList.Count > argOffset + 3 ? int.Parse(argList[argOffset + 3]) : 3600;

        // The routing table comes from the topology (self-consistent for whichever node we run as); the
        // built-in fallback keeps the old behaviour when no topology file is present.
        List<RoutingTableEntry> routingEntries = topology != null
            ? topology.BuildRoutingEntries(node)
            : BuildRoutingEntries(node);

        Console.WriteLine($"[runner] path={(legacy ? "legacy" : "seam")} connecting to {host}:{port} as node {node} for {(seconds > 0 ? seconds + "s" : "unlimited (Ctrl-C to stop)")}");

        // seconds <= 0 -> no timeout (run until the process is stopped); otherwise cancel after the window.
        using CancellationTokenSource cts = seconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(seconds))
            : new CancellationTokenSource();

        // The COSMOS file server, when a folder is configured. Built ONCE here and handed to
        // whichever transport path runs, so the two paths cannot drift into serving different things.
        FaServer? fileServer = TryCreateFileServer(topology);

        // The file push, when one was asked for. Built here - before any transport is chosen - so a
        // bad filespec or a missing file is reported immediately instead of after a link comes up.
        FaPushRun? pushRun = TryCreatePush(topology, node, pushFile, pushAs, pushToNode, pushOverwrite);
        if (pushFile != null && pushRun == null)
        {
            return 1;
        }

        // The pull, same reasoning: a bad filespec is reported now, not after a link comes up.
        FaPullRun? pullRun = TryCreatePull(topology, node, pullSpec, pullTo, pullFromNode, faProbe);
        if (pullSpec != null && pullRun == null)
        {
            return 1;
        }

        // --originate-from-seed belongs to the pull too. It used to reach only the sync daemon,
        // so a one-shot pull gated on CanReach and waited for a peer that speaks second - for
        // ever, and silently, because the "reading <file>" line comes after that gate.
        if (pullRun != null)
        {
            pullRun.OriginateFromSeed = originateFromSeed;
        }

        // And to the PUSH, for exactly the same reason. Fixing it in the pull alone left the flag
        // accepted-but-ignored on the push, which is worse than not supporting it: the one-shot
        // push reached LAPB Connected, announced, took XROUT letters from node 100 for six minutes
        // and never built a single FA frame, because the gate under it was still plain CanReach.
        // MEASURED 2026-08-18 against a live D100.
        if (pushRun != null)
        {
            pushRun.OriginateFromSeed = originateFromSeed;
        }

        // ONE TRANSFER AT A TIME. Both drivers originate their own conversation and both would
        // pump on the same tick, and nothing has ever been captured with two client conversations
        // running at once against one server. Refused rather than tried - a half-understood
        // interleaving that mostly works is the worst outcome, because it fails rarely and looks
        // like a protocol problem when it does.
        if (pushRun != null && pullRun != null)
        {
            Console.WriteLine(
                "[runner] --push and --pull cannot run together. Run one, then the other.");
            return 1;
        }

        // RELAY PATH: two HDLC links, so this node sits BETWEEN two machines instead of hanging off
        // one. Selected by --relay-listen; nothing else reaches it, so every existing path is
        // untouched.
        if (relayListen != null)
        {
            int listenPort;
            if (!int.TryParse(relayListen, out listenPort))
            {
                Console.WriteLine($"[runner] --relay-listen needs a port number, got '{relayListen}'");
                return 1;
            }

            // Which node is on each link cannot be inferred: the inbound peer DIALS us, so nothing
            // in our own configuration names it until it speaks. Stated explicitly rather than
            // guessed, because getting it wrong silently misroutes rather than failing.
            ushort inboundPeer;
            if (relayInboundPeer == null || !ushort.TryParse(relayInboundPeer, out inboundPeer))
            {
                Console.WriteLine("[runner] --relay-listen also needs --relay-inbound-node <nodeNumber>");
                return 1;
            }

            ushort outboundPeer = topology?.NeighbourId() ?? 100;

            // Not wired on the relay path either - a relay has two links and nothing here says
            // which one the file server is on. Say so instead of pushing down the wrong one.
            if (pushRun != null)
            {
                Console.WriteLine(
                    "[push] --push is not wired to the relay path yet (two links, and nothing "
                    + "names which one the file server is on). Nothing will be pushed on this run.");
            }

            if (pullRun != null)
            {
                Console.WriteLine(
                    "[pull] --pull is not wired to the relay path either, and for the same reason. "
                    + "Nothing will be read on this run.");
            }


            try
            {
                await RunRelayAsync(
                    topology, listenPort, host, port, node, inboundPeer, outboundPeer, routingEntries,
                    topology?.Motd, BuildTadUsers(topology), fileServer, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[runner] time window elapsed; stopping.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[runner] relay error: {ex.Message}");
            }

            Console.WriteLine("[runner] done.");
            return 0;
        }

        // ETHERNET PATH: when the topology says our neighbour is on a COSMOS segment there is no
        // HDLC bridge to dial at all - we join the segment instead. Everything above the link is
        // the same XmsgNodeHost the HDLC path uses.
        TopologyNode? ethernetPeer = topology?.PrimaryEthernetPeer();
        if (ethernetPeer != null && !legacy)
        {
            // The Ethernet link cannot address a peer out of nothing: a data frame carries the
            // peer's link id in its RECEIVER field, and that id is learned from the peer, never
            // derived. So the push waits for the far machine to send us something first - one
            // command typed at its console is enough - and only then starts the ladder.
            if (pushRun != null)
            {
                Console.WriteLine(
                    "[push] waiting for node " + ethernetPeer.Id + " to address us first. We cannot "
                    + "derive its link id; make it talk to us once (any command on its console that "
                    + "reaches this node) and the push starts by itself.");
            }

            if (pullRun != null)
            {
                Console.WriteLine(
                    "[pull] waiting for node " + ethernetPeer.Id + " to address us first. We cannot "
                    + "derive its link id; make it talk to us once (any command on its console that "
                    + "reaches this node) and the pull starts by itself.");
            }

            try
            {
                await RunEthernetSeamAsync(
                    topology!, ethernetPeer, node, routingEntries,
                    topology!.Motd, BuildTadUsers(topology), fileServer, pushRun, pullRun, traceFrames,
                    announceRestart, resyncHard, syncFolder, syncPullFolder, syncUser, syncToNode, batchInput, batchOutput, requestLink, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[runner] time window elapsed; stopping.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[runner] pump error: {ex.Message}");
            }

            Console.WriteLine("[runner] done.");
            return 0;
        }

        TcpBridgeTransport transport;
        try
        {
            transport = await TcpBridgeTransport.ConnectAsync(host, port, cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[runner] connect failed: {ex.Message}");
            return 1;
        }

        try
        {
            if (legacy)
            {
                await RunLegacyAsync(transport, node, routingEntries, cts.Token);
            }
            else
            {
                // --append-batch used to be refused here as Ethernet-only. It is now wired into
                // RunSeamAsync, because the refusal was missing plumbing rather than a protocol
                // limit: an XROUT letter to *XFTRA does not care which transport carries it. That
                // is FOUR flags found wired to the Ethernet seam alone - --announce-restart with
                // --resync-hard, the link-seed store, --sync, and this one.
                //
                // The rule that found all four still stands: a flag accepted and IGNORED costs more
                // than one refused, because it gets reported as working.

                // Resolve the batch target: --batch-to when given, else the adjacent peer on this
                // link (the one machine a point-to-point bridge can mean), else ourselves - which
                // AppendRemoteBatchRun will then fail on loudly rather than silently.
                ushort resolvedBatchNode;
                if (batchToNode == null || !ushort.TryParse(batchToNode, out resolvedBatchNode))
                {
                    resolvedBatchNode = node;
                    for (int i = 0; i < routingEntries.Count; i++)
                    {
                        if (routingEntries[i].System != node && routingEntries[i].Hops == 1)
                        {
                            resolvedBatchNode = routingEntries[i].System;
                            break;
                        }
                    }
                }

                RetroXmsg.Runner.TopologyNode? batchPeerNode =
                    topology?.FindById(resolvedBatchNode);
                string resolvedBatchMachine =
                    (batchPeerNode != null && !string.IsNullOrEmpty(batchPeerNode.Alias))
                        ? batchPeerNode.Alias!.ToUpperInvariant()
                        : "D" + resolvedBatchNode;

                // Resolve the sync target HERE, where the topology is in scope; RunSeamAsync takes
                // the pieces rather than the whole topology.
                ushort resolvedSyncNode;
                if (syncToNode == null || !ushort.TryParse(syncToNode, out resolvedSyncNode))
                {
                    resolvedSyncNode = node;
                }

                RetroXmsg.Runner.TopologyNode? syncPeerNode =
                    topology?.FindById(resolvedSyncNode);
                string resolvedSyncMachine =
                    (syncPeerNode != null && !string.IsNullOrEmpty(syncPeerNode.Alias))
                        ? syncPeerNode.Alias!.ToUpperInvariant()
                        : "D" + resolvedSyncNode;

                // A one-shot gets its own, much shorter bound; a daemon keeps the full window.
                // The transfer normally ends the run itself the moment it finishes, so this only
                // ever fires when something is stuck.
                using CancellationTokenSource oneShotBound =
                    CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                if (pushRun != null || pullRun != null)
                {
                    oneShotBound.CancelAfter(TimeSpan.FromSeconds(transferTimeout));
                }

                await RunSeamAsync(transport, host, port, node, routingEntries, topology?.Motd,
                    BuildTadUsers(topology), fileServer, pushRun, pullRun,
                    announceRestart, resyncHard, originateFromSeed,
                    syncFolder, syncPullFolder, resolvedSyncMachine, syncUser ?? "SYSTEM", resolvedSyncNode,
                    batchInput, batchOutput, resolvedBatchMachine, resolvedBatchNode,
                    chatLoad != null ? int.Parse(chatLoad) : 0,
                    // ONE SERVICE NAME, and the rooms are separate. --chat-room now names ROOMS -
                    // one, or several separated by commas - and they travel inside the Join rather
                    // than being XROUT names of their own.
                    chatService ?? "*CHAT",
                    SplitRooms(chatRoom),
                    chatToNode != null ? ushort.Parse(chatToNode) : node,
                    resolvedBatchMachine,
                    chatLines != null ? int.Parse(chatLines) : 5,
                    oneShotBound.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[runner] time window elapsed; stopping.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[runner] pump error: {ex.Message}");
        }
        finally
        {
            transport.Dispose();
        }

        // A ONE-SHOT THAT NEVER FINISHED MUST NOT EXIT 0.
        //
        // It used to. The runner printed "done." and returned success whatever had happened, so
        // every caller had to guess from the side effects - and tools/planc-build.ps1 guessed with
        // Test-Path, which says only that SOME file is there, not that this run wrote it. On
        // 2026-08-18 that read two listings from the previous evening as the output of a compile
        // that had just run, and the wrong conclusion drawn from them cost a round of re-checking
        // on the machine.
        //
        // Finished covers both endings - a completed transfer and a refused one. Neither having
        // happened means the run was cut off with the question still open, and that is a failure.
        // FINISHED IS NOT THE SAME AS WORKED, and reading it as success was a hole in the first
        // version of this check. Finished answers "is it over" - a refusal ends a transfer just as
        // definitely as a completed one does. MEASURED 2026-08-18: a push the machine refused with
        // SINTRAN error 39 exited 0 through exactly this test.
        if (pushRun != null && pushRun.Failed)
        {
            Console.WriteLine("[runner] the push FAILED - see the *** FAILED *** line above.");

            // NAME THE CURE FOR THE ONE REFUSAL THAT HAS ONE. Error 62 means the file is already
            // on the machine and we asked to create it - which is the commonest thing to get
            // wrong, because carrying the same source over twice is the normal way to use this.
            // The number is decoded already; a person reading "File already exists" still has to
            // know that quoting is what asked for a create, and that is a lot to expect.
            if (!pushOverwrite && pushRun.SintranError == 62)
            {
                Console.WriteLine(
                    "[runner] that name is already on the machine - add --push-overwrite to replace it.");
            }

            return 1;
        }

        if (pullRun != null && pullRun.Failed)
        {
            Console.WriteLine("[runner] the pull FAILED - see the *** FAILED *** line above.");
            return 1;
        }

        if (pushRun != null && !pushRun.Finished)
        {
            Console.WriteLine(
                $"[runner] the push did NOT finish within {transferTimeout}s - nothing was confirmed " +
                "written. Raise --transfer-timeout for a very large file, or check the link.");
            return 1;
        }

        if (pullRun != null && !pullRun.Finished)
        {
            Console.WriteLine(
                $"[runner] the pull did NOT finish within {transferTimeout}s - no file was written. " +
                "Raise --transfer-timeout for a very large file, or check the link.");
            return 1;
        }

        Console.WriteLine("[runner] done.");
        return 0;
    }

    /// <summary>
    /// Builds the file push when <c>--push</c> was given, or reports why it cannot be built.
    /// </summary>
    /// <param name="topology">
    /// The loaded topology, used for our own system name and to default the server node.
    /// </param>
    /// <param name="node">
    /// The system number this runner presents on the wire.
    /// </param>
    /// <param name="pushFile">
    /// The local file to send, or null when no push was asked for.
    /// </param>
    /// <param name="pushAs">
    /// The name the file takes on the machine, for example <c>PUSHED:DATA</c>. Defaults to the
    /// local file's own name converted to SINTRAN form.
    /// </param>
    /// <param name="pushToNode">
    /// The node running <c>*FA-SERVER</c>, as a number. Defaults to the topology's neighbour.
    /// </param>
    /// <param name="overwrite">
    /// True to replace a file that is already on the machine, false to create a new one.
    /// </param>
    /// <returns>
    /// The push, or null when none was asked for OR when it could not be built.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The filespec is LOCAL to the server: <c>PUSHED:DATA</c>, never
    /// <c>D102(SYSTEM)."PUSHED:DATA"</c>. The conversation is already addressed to that machine and
    /// the user travels separately in the reserve request, so a machine-qualified name is
    /// command-line syntax that has no place on the FA wire. It is also too long for the field.
    /// </para>
    /// <para>
    /// Everything is checked HERE, before a link is brought up, because the alternative is a
    /// twenty-second wait followed by an argument error.
    /// </para>
    /// </remarks>
    private static FaPushRun? TryCreatePush(
        TopologyConfig? topology, ushort node, string? pushFile, string? pushAs, string? pushToNode,
        bool overwrite)
    {
        if (pushFile == null)
        {
            return null;
        }

        if (!System.IO.File.Exists(pushFile))
        {
            Console.WriteLine($"[push] there is no file at '{pushFile}'");
            return null;
        }

        // Which machine runs the file server. The topology's neighbour is the sensible default -
        // it is the only node we have a link to - but it is stated in the log either way, because
        // pushing to the wrong machine writes a real file in a real user's directory.
        ushort serverNode;
        if (pushToNode != null)
        {
            if (!ushort.TryParse(pushToNode, out serverNode))
            {
                Console.WriteLine($"[push] --push-to needs a node number, got '{pushToNode}'");
                return null;
            }
        }
        else
        {
            serverNode = topology?.NeighbourId() ?? 100;
        }

        // The connect letter names the machine we are asking, NOT ourselves - every capture we
        // hold agrees, and naming ourselves is what D100 answered with a network error. Taken
        // from the topology's alias for that node when it has one.
        TopologyNode? server = topology?.FindById(serverNode);
        string serverName = (server != null && !string.IsNullOrEmpty(server.Alias))
            ? server.Alias!.ToUpperInvariant()
            : "D" + serverNode;

        // Our own name is still worth printing: the remote must have been told about it with
        // DEF-REMOTE or it cannot answer us at all, whatever the letter says.
        TopologyNode? self = topology?.FindById(node);
        string ourName = (self != null && !string.IsNullOrEmpty(self.Alias))
            ? self.Alias!.ToUpperInvariant()
            : "D" + node;

        // The name on the machine. Given explicitly, or taken from the local file with the type
        // separator swapped from a dot to a colon.
        string fileSpec;
        if (pushAs != null)
        {
            fileSpec = pushAs.ToUpperInvariant();
        }
        else
        {
            string leaf = System.IO.Path.GetFileName(pushFile);
            string name;
            string type;
            string problem;
            if (!RetroIO.Protocols.Xmsg.Sync.SintranFileName.TryConvert(
                    leaf, out name, out type, out problem))
            {
                Console.WriteLine($"[push] '{leaf}' cannot be a SINTRAN name: {problem}");
                Console.WriteLine("[push] give one with --push-as, for example --push-as PUSHED:DATA");
                return null;
            }

            fileSpec = RetroIO.Protocols.Xmsg.Sync.SintranFileName.ToFileSpec(name, type);
        }

        byte[] content;
        try
        {
            content = System.IO.File.ReadAllBytes(pushFile);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[push] cannot read '{pushFile}': {ex.Message}");
            return null;
        }

        // CREATE OR REPLACE, and the quotes are the whole difference.
        //
        // A quoted name asks SINTRAN to CREATE the file; an unquoted one opens what is already
        // there. That is the command-line rule, and it is now confirmed on the wire in both
        // directions: a quoted push at a name that exists is refused with SINTRAN error 62,
        // "File already exists" (MEASURED 2026-08-18, pushing CHATSV:PLNC over itself).
        //
        // This used to quote unconditionally, with a comment saying the rule was unverified. It
        // is verified now, and the unconditional quoting was a defect rather than a caution: a
        // one-shot push could create a file but could never replace one, so the ordinary case of
        // carrying a source file over again - which is what this tool is FOR - always failed. The
        // sync daemon had the choice all along; the one-shot did not.
        //
        // Explicit rather than a retry-on-62. The daemon retries because it is unattended and has
        // a ledger to learn into; a one-shot is a person at a keyboard, and silently replacing a
        // file they asked to create is the wrong way round to be helpful.
        string quoted = overwrite ? fileSpec : "\"" + fileSpec + "\"";

        try
        {
            FaWriteTarget target = new FaWriteTarget(serverNode, serverName, quoted);
            Console.WriteLine(
                $"[push] {pushFile} -> node {serverNode} ({serverName}) user {target.User} " +
                $"as {quoted} (we are {ourName})");
            return new FaPushRun(pushFile, content, target);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[push] {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds the file pull when <c>--pull</c> was given, or reports why it cannot be built.
    /// </summary>
    /// <param name="topology">
    /// The loaded topology, or null when there is none.
    /// </param>
    /// <param name="node">
    /// Our own node number, for the log.
    /// </param>
    /// <param name="pullSpec">
    /// The file to read AS THE REMOTE MACHINE NAMES IT, or null when no pull was asked for.
    /// </param>
    /// <param name="pullTo">
    /// Where to write it locally, or null to use the filespec with the colon turned into a dot.
    /// </param>
    /// <param name="pullFromNode">
    /// The node to read from, or null to use the topology's neighbour.
    /// </param>
    /// <param name="probeWithoutOpen">
    /// <c>true</c> to run the diagnostic probe - reserve a file entry and set the block size with
    /// no <c>OpenFile</c> - instead of transferring anything.
    /// </param>
    /// <returns>
    /// The pull, or null when none was asked for OR when it could not be built.
    /// </returns>
    /// <remarks>
    /// <para><b>The name is NOT quoted, unlike a push</b></para>
    /// <para>
    /// A push creates a file, and SINTRAN's rule is that quotes go around a name being created. A
    /// pull opens one that exists, and the captured reader sends the name bare. That also buys two
    /// characters: a read tolerates thirteen where a write allows eleven.
    /// </para>
    /// </remarks>
    private static FaPullRun? TryCreatePull(
        TopologyConfig? topology,
        ushort node,
        string? pullSpec,
        string? pullTo,
        string? pullFromNode,
        bool probeWithoutOpen)
    {
        if (pullSpec == null)
        {
            return null;
        }

        // Which machine to read from. Same default and the same reason as the push: the topology's
        // neighbour is the only node we have a link to, and it is stated in the log either way.
        ushort serverNode;
        if (pullFromNode != null)
        {
            if (!ushort.TryParse(pullFromNode, out serverNode))
            {
                Console.WriteLine($"[pull] --pull-from needs a node number, got '{pullFromNode}'");
                return null;
            }
        }
        else
        {
            serverNode = topology?.NeighbourId() ?? 100;
        }

        // The connect letter names the machine we are asking, NOT ourselves.
        TopologyNode? server = topology?.FindById(serverNode);
        string serverName = (server != null && !string.IsNullOrEmpty(server.Alias))
            ? server.Alias!.ToUpperInvariant()
            : "D" + serverNode;

        TopologyNode? self = topology?.FindById(node);
        string ourName = (self != null && !string.IsNullOrEmpty(self.Alias))
            ? self.Alias!.ToUpperInvariant()
            : "D" + node;

        string fileSpec = pullSpec.ToUpperInvariant();

        // Where it lands here. The filespec with the type separator swapped back from a colon to a
        // dot, which is the inverse of what the push does to a local name.
        string localPath = pullTo ?? fileSpec.Replace(':', '.');

        // REFUSED RATHER THAN OVERWRITTEN. A pull that quietly replaced a local file would be a
        // destructive default, and the whole point of pulling is usually to compare the result
        // against what is already there. A probe writes nothing, so it has nothing to protect.
        if (!probeWithoutOpen && System.IO.File.Exists(localPath))
        {
            Console.WriteLine($"[pull] '{localPath}' already exists; move it or give --pull-to");
            return null;
        }

        try
        {
            FaReadSource source = new FaReadSource(serverNode, serverName, fileSpec);
            if (probeWithoutOpen)
            {
                // Say plainly that this is not a transfer, and say what it is for. A log that only
                // showed "[pull] ..." would read as a pull that mysteriously stopped after two
                // steps, which is exactly what a probe looks like from the outside.
                Console.WriteLine(
                    $"[fa-probe] NOT A TRANSFER. Reserve + SetBlockSize with NO OpenFile, against " +
                    $"{fileSpec} on node {serverNode} ({serverName}) user {source.User} " +
                    $"(we are {ourName})");
                Console.WriteLine(
                    "[fa-probe] the file MUST exist. Reading the A2 value on the SetBlockSize " +
                    "reply: 4104 means 'no file open', anything else means 'an earlier step failed'.");
            }
            else
            {
                Console.WriteLine(
                    $"[pull] {fileSpec} on node {serverNode} ({serverName}) user {source.User} " +
                    $"-> {localPath} (we are {ourName})");
            }

            return new FaPullRun(localPath, source, probeWithoutOpen);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[pull] {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Removes a named <c>--option value</c> pair from the argument list and returns its value.
    /// </summary>
    /// <param name="args">
    /// The mutable argument list; the option name and its value are removed in place when found.
    /// </param>
    /// <param name="name">
    /// The option name to look for (for example <c>--config</c>), matched case-insensitively.
    /// </param>
    /// <returns>
    /// The value following the option, or <c>null</c> when the option is absent.
    /// </returns>
    /// <summary>
    /// Takes a valueless flag out of the argument vector.
    /// </summary>
    /// <param name="args">
    /// The remaining arguments; the flag is removed when found.
    /// </param>
    /// <param name="name">
    /// The flag, for example <c>--trace-frames</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the flag was present.
    /// </returns>
    private static bool TakeFlag(List<string> args, string name)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                args.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    private static string? TakeOption(List<string> args, string name)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                string? value = (i + 1 < args.Count) ? args[i + 1] : null;
                args.RemoveAt(i);              // remove the option name
                if (value != null)
                {
                    args.RemoveAt(i);          // remove the value that followed it
                }

                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Prints the startup banner: the runner version and build time, plus the full folders (and paths)
    /// of the config file and the log file.
    /// </summary>
    /// <param name="configPath">
    /// The resolved topology config file path (its folder is reported).
    /// </param>
    /// <param name="fileWriter">
    /// The active log writer, or <c>null</c> when file logging is off (then the log folder is reported as off).
    /// </param>
    /// <summary>
    /// How many FA connection numbers each run reserves up front.
    /// </summary>
    /// <remarks>
    /// Reserved in BLOCKS rather than saved per connection, so a runner that is killed - which is
    /// how it usually ends - still never hands out a number twice.
    /// </remarks>
    private const ushort ConnectionNumberBlock = 64;

    /// <summary>
    /// The lowest connection number handed out, and where the counter wraps back to.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>FaServer</c>'s own range. Kept here too because this method decides what the
    /// NEXT process starts from, and a stale file written before the wrap existed can still hold a
    /// number from the climbing days.
    /// </remarks>
    private const ushort FirstConnectionNumber = 0x0042;

    /// <summary>
    /// The highest connection number handed out before wrapping.
    /// </summary>
    /// <remarks>
    /// Every connection number a real machine has sent us is small - 0x0004 through 0x0046 - and a
    /// climbed 0x0E02 was silently ignored by a live D100. Where the true limit sits is NOT known;
    /// this keeps us in the captures' order of magnitude. See <c>FaServer.LastConnectionNumber</c>.
    /// </remarks>
    private const ushort LastConnectionNumber = 0x00FF;

    /// <summary>
    /// Takes the next block of FA connection numbers, so a restart never reuses one.
    /// </summary>
    /// <returns>
    /// The number the file server should answer its next connect letter with.
    /// </returns>
    /// <remarks>
    /// <para><b>Why this exists</b></para>
    /// The connection number counts up per CONNECTION and carries across conversations - its scope
    /// is the server's lifetime, unlike the two per-conversation counters, which reset with the
    /// conversation. A real server stays up; ours restarts constantly and began at <c>0x0042</c>
    /// every time, handing back numbers the peer had already seen. Persisting it is what the
    /// observed semantics ask for. See <c>FaServer.NextConnectionNumber</c>.
    /// <para><b>It does NOT fix the first-connect stall - that theory was DISPROVED</b></para>
    /// The stall was the reason this was written: after a runner restart the first connect letter
    /// gets its confirmation and then nothing follows, and a retry minutes later works. Reusing a
    /// connection number looked like the cause, because the retry that worked had used the NEXT
    /// number and because restarting XMSG on D100 also cured it.
    /// <para>
    /// Tested 2026-08-06: with the state file in place a restart answered the first connect with a
    /// fresh <c>0x0082</c> and it stalled anyway. So the cause is something else that resets when
    /// this process does - the session wire port, which also restarts at <c>0x0211</c> every run, is
    /// the next candidate and is UNTESTED. Do not repeat the connection-number theory.
    /// </para>
    /// <para><b>Block reservation, not save-on-use</b></para>
    /// The file records where the NEXT run must start. Reserving
    /// <see cref="ConnectionNumberBlock"/> up front lets a run hand out that many without touching
    /// the file again, and being killed loses nothing - the worst case is skipping some numbers,
    /// which costs nothing.
    /// </remarks>
    private static ushort ReserveConnectionNumbers()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "fa-connection.state");

        ushort start = FirstConnectionNumber;
        try
        {
            if (System.IO.File.Exists(path))
            {
                string text = System.IO.File.ReadAllText(path).Trim();
                ushort saved;
                if (ushort.TryParse(text, out saved) && saved != 0)
                {
                    start = saved;
                }
            }

            // WRAP, do not climb. This used to be a plain add, so every process start pushed the
            // number 64 higher for ever and it reached 0x0E02 - far outside anything a real machine
            // sends. A live D100 then ignored our connect confirmation entirely: no error, no
            // reject, it just re-sent its letter until the terminal timed out. Measured 2026-08-09.
            ushort next = (ushort)(start + ConnectionNumberBlock);
            if (start < FirstConnectionNumber || start > LastConnectionNumber
                || next > LastConnectionNumber)
            {
                // Either the file is stale from the climbing days, or this block runs off the end.
                // Either way the next run starts over rather than leaving the range.
                if (start < FirstConnectionNumber || start > LastConnectionNumber)
                {
                    start = FirstConnectionNumber;
                }

                next = FirstConnectionNumber;
            }

            System.IO.File.WriteAllText(path, next.ToString());
        }
        catch (System.IO.IOException error)
        {
            // A state file we cannot read or write is not worth failing the run for - the cost is
            // one stalled connect, which the client retries past.
            Console.WriteLine($"[fa] connection-number state file unusable ({error.Message}); starting at 0x{start:X4}");
        }

        Console.WriteLine(
            $"[fa] connection numbers 0x{start:X4}..0x{(ushort)(start + ConnectionNumberBlock - 1):X4} (state: {path})");
        return start;
    }

    private static void PrintStartupBanner(string configPath, RotatingFileWriter? fileWriter)
    {
        System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
        Version? version = asm.GetName().Version;

        // Build time = the executable's last-write time.
        //
        // NOT Assembly.Location: it returns an EMPTY string for an assembly embedded in a
        // single-file app, which is how these are published (build.ps1), so the banner would have
        // said "unknown" on every shipped build and only shown a real date to developers running
        // from bin\. The analyzer says so outright - IL3000. Environment.ProcessPath is the path of
        // the running process itself and is correct in both layouts.
        string? exePath = Environment.ProcessPath;
        string built = !string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath)
            ? System.IO.File.GetLastWriteTime(exePath).ToString("yyyy-MM-dd HH:mm:ss")
            : "unknown";

        string configFolder = System.IO.Path.GetDirectoryName(configPath) ?? configPath;
        string logFolder = fileWriter != null
            ? (System.IO.Path.GetDirectoryName(fileWriter.FilePath) ?? fileWriter.FilePath)
            : "(file logging off)";
        string logFile = fileWriter != null ? fileWriter.FilePath : "(file logging off)";

        Console.WriteLine($"[runner] xmsgrunner v{version} built {built}");
        Console.WriteLine($"[runner] config folder: {configFolder}");
        Console.WriteLine($"[runner] config file:   {configPath}");
        Console.WriteLine($"[runner] log folder:    {logFolder}");
        Console.WriteLine($"[runner] log file:      {logFile}");

        // The RAW command line, verbatim. This exists because six runs of this program were compared
        // after the fact to explain why a second process was refused, and NONE of them recorded which
        // options they were given - so the one field that mattered (whether --resync-hard zeroed our
        // outgoing Flags1, and whether --request-link asked the peer to reset its own expectation)
        // could not be recovered from the logs at all. Several hours of wrong explanations came out
        // of that gap. Print the argv itself rather than re-printing the parsed flags: a hand-written
        // list of flags can drift out of step with the parsing code above and quietly lie, whereas
        // the argv cannot.
        string[] commandLine = Environment.GetCommandLineArgs();
        Console.WriteLine($"[runner] command line:  {string.Join(" ", commandLine)}");
    }

    /// <summary>
    /// Loads the topology file when present, returning <c>null</c> (with a console note) on any problem so
    /// the runner falls back to built-in defaults rather than failing to start.
    /// </summary>
    /// <param name="path">
    /// The resolved topology file path.
    /// </param>
    /// <param name="console">
    /// The console writer used for the load note (logging is not set up yet at this point).
    /// </param>
    /// <returns>
    /// The parsed topology, or <c>null</c> when the file is missing or invalid.
    /// </returns>
    private static TopologyConfig? TryLoadTopology(string path, System.IO.TextWriter console)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                console.WriteLine($"[runner] no topology file at {path}; using built-in defaults.");
                return null;
            }

            TopologyConfig config = TopologyConfig.Load(path);
            console.WriteLine($"[runner] topology loaded: self={config.Self}, {config.Nodes.Count} node(s), file={path}");
            return config;
        }
        catch (Exception ex)
        {
            console.WriteLine($"[runner] topology load failed ({ex.Message}); using built-in defaults.");
            return null;
        }
    }

    /// <summary>
    /// Builds the TAD login directory from the topology's <c>tadUsers</c> section.
    /// </summary>
    /// <remarks>
    /// A user whose password is empty or missing is passwordless (login skips the password prompt). When
    /// the section is absent or empty the directory falls back to a single <c>SYSTEM</c>/<c>SYSTEM</c>
    /// account so the server always has a login. Entries with a blank username are skipped.
    /// </remarks>
    /// <param name="topology">
    /// The loaded topology (may be <c>null</c>).
    /// </param>
    /// <returns>
    /// The TAD user directory.
    /// </returns>
    private static TadUserDirectory BuildTadUsers(TopologyConfig? topology)
    {
        if (topology == null || topology.TadUsers == null || topology.TadUsers.Count == 0)
        {
            return new TadUserDirectory();   // default SYSTEM/SYSTEM
        }

        List<TadUser> users = new List<TadUser>(topology.TadUsers.Count);
        for (int i = 0; i < topology.TadUsers.Count; i++)
        {
            TadUserConfig config = topology.TadUsers[i];
            if (string.IsNullOrWhiteSpace(config.Username))
            {
                continue;
            }

            users.Add(new TadUser(config.Username.Trim(), config.Password));
        }

        return users.Count != 0 ? new TadUserDirectory(users) : new TadUserDirectory();
    }

    /// <summary>
    /// Creates the COSMOS file server (<c>*FA-SERVER</c>) over the configured Windows folder.
    /// </summary>
    /// <param name="topology">
    /// The loaded topology (may be <c>null</c>).
    /// </param>
    /// <returns>
    /// The file server, or <c>null</c> when no folder is configured, the block is disabled, or the
    /// folder cannot be opened.
    /// </returns>
    /// <remarks>
    /// <b>No folder configured means NO file server</b>, and a line in the log saying so. Falling
    /// back to a folder next to the executable would share the runner's own logs, state files and
    /// DLLs with any machine that asked, which nobody chose. Sharing files is an explicit act.
    /// </remarks>
    private static FaServer? TryCreateFileServer(TopologyConfig? topology)
    {
        FileServerConfig? config = topology?.FileServer;
        if (config == null || string.IsNullOrWhiteSpace(config.Root))
        {
            Console.WriteLine(
                "[fa] file server OFF: no \"fileServer\": { \"root\": \"...\" } in the topology file. "
                + "Nothing is served. Set a folder to turn it on.");
            return null;
        }

        if (!config.Enabled)
        {
            Console.WriteLine($"[fa] file server OFF: \"enabled\" is false (the configured folder was {config.Root}).");
            return null;
        }

        // A relative folder lands next to the executable; an absolute path is honoured as-is - the
        // same rule the log file uses, so one convention covers both.
        string root = System.IO.Path.IsPathRooted(config.Root)
            ? config.Root
            : System.IO.Path.Combine(AppContext.BaseDirectory, config.Root);

        try
        {
            FolderFileStore store = new FolderFileStore(root);
            FaServer server = new FaServer(store, config.UserIndex);
            server.NextConnectionNumber = ReserveConnectionNumbers();
            server.Log += line => Console.WriteLine(line);
            Console.WriteLine($"[fa] file server ON as {FaServer.ServerName} (logical port {FaServer.ServerLogicalPort}, wire port 0x{FaServer.FaServerWirePort:X4})");
            Console.WriteLine($"[fa] serving folder: {store.RootFolder}");
            Console.WriteLine($"[fa] files visible now: {store.ListFiles().Count}");
            return server;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[fa] file server OFF: folder '{root}' could not be opened ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Builds the server list handed to the node host: the TAD server always, plus the file server
    /// when one is configured.
    /// </summary>
    /// <param name="tadServer">
    /// The TAD terminal server.
    /// </param>
    /// <param name="fileServer">
    /// The COSMOS file server, or <c>null</c> when it is off.
    /// </param>
    /// <returns>
    /// The servers to register, in order.
    /// </returns>
    private static IXmsgServer[] BuildServerList(TadServer tadServer, FaServer? fileServer)
    {
        if (fileServer == null)
        {
            return new IXmsgServer[] { tadServer };
        }

        return new IXmsgServer[] { tadServer, fileServer };
    }

    /// <summary>
    /// Builds the two node hosts a relay runs on - one per link - with the SAME servers registered
    /// on both.
    /// </summary>
    /// <param name="inbound">
    /// The link the relay listens on.
    /// </param>
    /// <param name="outbound">
    /// The link the relay dials out on.
    /// </param>
    /// <param name="node">
    /// Our own node number.
    /// </param>
    /// <param name="routingEntries">
    /// The routing table both hosts answer reachability questions from.
    /// </param>
    /// <param name="sequenceStore">
    /// The persisted responder sequence, shared by both hosts as it always has been.
    /// </param>
    /// <param name="servers">
    /// The servers to register on both hosts.
    /// </param>
    /// <returns>
    /// Two hosts: the inbound one at index 0, the outbound one at index 1.
    /// </returns>
    /// <remarks>
    /// <para><b>Why this is a method and not two lines at the call site</b></para>
    /// <para>
    /// Because the two lines disagreed, and nothing noticed for as long as the relay was only ever
    /// asked to FORWARD. The outbound host was built with no server list at all, so a datagram a
    /// peer on that link addressed TO US - as opposed to asking us to pass it on - reached
    /// <c>XmsgServerHost.Route</c>, found no server to hand it to, and produced nothing.
    /// </para>
    /// <para>
    /// What that looked like from the outside, on 2026-08-08: typing
    /// <c>LI-FI D19999(SYSTEM).,,</c> on D100 hung the terminal until SINTRAN gave up with
    /// "NO ANSWER FROM REMOTE SYSTEM; FILE-ACCESS CONNECTION ABORTED". The capture
    /// (<c>DOC/captures/FA-OPERATIONS-2026-08-08</c>) shows the reachability request answered
    /// normally, then the *FA-SERVER connect letter - body <c>1B41 0014 FF0A</c> "*FA-SERVER"
    /// <c>FE06</c> "D19999" - answered with a BARE ACK and nothing else. The relay counters never
    /// move during any of it, because a datagram addressed to our own node is not transit, so the
    /// log was silent on both sides.
    /// </para>
    /// <para>
    /// ONE set of server instances, not one per link. A session is keyed by its wire port and
    /// records the node and system it belongs to, and every reply is built through whichever host
    /// routed the request - so answers still leave by the link they arrived on, and a client that
    /// reaches us over either link talks to the same served folder.
    /// </para>
    /// </remarks>
    internal static XmsgNodeHost[] BuildRelayHosts(
        ILink inbound,
        ILink outbound,
        ushort node,
        IReadOnlyList<RoutingTableEntry> routingEntries,
        IResponderSequenceStore sequenceStore,
        IXmsgServer[] servers)
    {
        return new XmsgNodeHost[]
        {
            new XmsgNodeHost(inbound, node, routingEntries, sequenceStore, servers),
            new XmsgNodeHost(outbound, node, routingEntries, sequenceStore, servers),
        };
    }

    /// <summary>
    /// Creates the rotating log-file writer from the topology's log section, or <c>null</c> when logging is
    /// disabled or the file cannot be opened.
    /// </summary>
    /// <param name="topology">
    /// The loaded topology (may be <c>null</c>, in which case the default log config is used).
    /// </param>
    /// <param name="console">
    /// The console writer used to report a log-open failure.
    /// </param>
    /// <returns>
    /// A rotating writer, or <c>null</c> when file logging is off.
    /// </returns>
    private static RotatingFileWriter? TryCreateLogWriter(TopologyConfig? topology, System.IO.TextWriter console)
    {
        LogConfig log = topology?.Log ?? new LogConfig();
        if (!log.Enabled || log.LevelKind() == LogLevel.Off)
        {
            return null;
        }

        try
        {
            // A relative file name lands next to the executable; an absolute path is honoured as-is.
            string path = System.IO.Path.IsPathRooted(log.File)
                ? log.File
                : System.IO.Path.Combine(AppContext.BaseDirectory, log.File);
            RotatingFileWriter writer = new RotatingFileWriter(path, log.MaxBytes, log.Keep);
            console.WriteLine($"[runner] logging to {path} (level {log.LevelKind()}, max {log.MaxSizeMb} MB, keep {log.Keep}).");
            return writer;
        }
        catch (Exception ex)
        {
            console.WriteLine($"[runner] file logging disabled ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Resolves the effective self node: <c>--self</c> (id or alias) wins, then the positional node arg,
    /// then the topology's <c>self</c>, then the historical default of 103.
    /// </summary>
    /// <param name="topology">
    /// The loaded topology, used to resolve a <c>--self</c> alias and to supply the default self.
    /// </param>
    /// <param name="selfArg">
    /// The raw <c>--self</c> value (id or alias), or <c>null</c> when not given.
    /// </param>
    /// <param name="argList">
    /// The positional argument list (after option extraction).
    /// </param>
    /// <param name="argOffset">
    /// The index offset for the positional args (1 when the "legacy" keyword is present, else 0).
    /// </param>
    /// <returns>
    /// The node id the runner should present on the wire.
    /// </returns>
    /// <exception cref="FormatException">
    /// Thrown when <paramref name="selfArg"/> is neither a known alias/id nor a parseable number.
    /// </exception>
    private static ushort ResolveSelf(TopologyConfig? topology, string? selfArg, List<string> argList, int argOffset)
    {
        if (!string.IsNullOrWhiteSpace(selfArg))
        {
            TopologyNode? found = topology?.Find(selfArg);
            if (found != null)
            {
                return found.Id;
            }

            if (ushort.TryParse(selfArg, out ushort selfId))
            {
                return selfId;
            }

            throw new FormatException($"--self '{selfArg}' is not a known node id or alias.");
        }

        if (argList.Count > argOffset + 2)
        {
            return (ushort)int.Parse(argList[argOffset + 2]);
        }

        if (topology != null)
        {
            return topology.Self;
        }

        return 103;
    }

    /// <summary>
    /// Parses the client-mode arguments, connects the bridge, and runs the connect-to asker.
    /// </summary>
    /// <param name="args">
    /// The argument list (args[0] == "client"), after option extraction.
    /// </param>
    /// <param name="topology">
    /// The loaded topology (may be <c>null</c>), used to default the host/port and node numbers.
    /// </param>
    /// <returns>
    /// The process exit code.
    /// </returns>
    private static async Task<int> RunClientModeAsync(List<string> args, TopologyConfig? topology)
    {
        // Topology-derived defaults: dial the neighbour's bridge, present our own self node, connect to
        // the neighbour host node. Positional args still override every one of these.
        TopologyEndpoint? endpoint = topology?.PrimaryEndpoint();
        ushort defaultOwn = topology?.Self ?? 102;
        ushort defaultHostNode = topology?.NeighbourId() ?? 100;

        string host = args.Count > 1 ? args[1] : (endpoint?.Host ?? "127.0.0.1");
        int port = args.Count > 2 ? int.Parse(args[2]) : (endpoint?.Port ?? 10362);
        ushort ownNode = (ushort)(args.Count > 3 ? int.Parse(args[3]) : defaultOwn);
        string targetName = args.Count > 4 ? args[4] : "D100";
        ushort hostNode = (ushort)(args.Count > 5 ? int.Parse(args[5]) : defaultHostNode);
        // Link seed (hex). Default from the observed node pairs: 100-102 = 0x14, 100-103 = 0x13.
        byte seed = args.Count > 6 ? Convert.ToByte(args[6], 16) : (ownNode == 103 ? (byte)0x13 : (byte)0x14);

        Console.WriteLine($"[client] connecting bridge {host}:{port} as node {ownNode}; connect-to '{targetName}' on host node {hostNode}; seed 0x{seed:X2}");

        using CancellationTokenSource cts = new CancellationTokenSource();
        TcpBridgeTransport transport;
        try
        {
            transport = await TcpBridgeTransport.ConnectAsync(host, port, cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[client] connect failed: {ex.Message}");
            return 1;
        }

        try
        {
            await RunClientAsync(transport, host, port, ownNode, hostNode, targetName, seed, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[client] stopped.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[client] error: {ex.Message}");
        }
        finally
        {
            transport.Dispose();
        }

        return 0;
    }

    /// <summary>
    /// Runs the connect-to CLIENT (asker): TcpBridgeTransport -> LapbLayerAdapter -> XmsgCodec ->
    /// TadAskerSession. Sends the connect letter when the link comes up, drives the TAD handshake,
    /// renders host terminal text, and forwards stdin lines as keystroke frames.
    /// </summary>
    /// <param name="transport">
    /// The connected bridge transport.
    /// </param>
    /// <param name="host">
    /// The bridge host (for the link id).
    /// </param>
    /// <param name="port">
    /// The bridge port (for the link id).
    /// </param>
    /// <param name="ownNode">
    /// This client's node number.
    /// </param>
    /// <param name="hostNode">
    /// The host node we connect to.
    /// </param>
    /// <param name="targetName">
    /// The remote name carried in the connect letter.
    /// </param>
    /// <param name="token">
    /// A token that stops the session.
    /// </param>
    /// <returns>
    /// A task that completes when the pump stops.
    /// </returns>
    private static async Task RunClientAsync(
        TcpBridgeTransport transport, string host, int port, ushort ownNode, ushort hostNode, string targetName, byte seed, CancellationToken token)
    {
        string linkId = $"hdlc:{host}:{port}";

        LapbLayer link = new LapbLayer(ownNode);
        LapbLayerAdapter adapter = new LapbLayerAdapter(linkId, transport, link);
        LinkXmsgTransport codecTransport = new LinkXmsgTransport(adapter);
        XmsgCodec codec = new XmsgCodec(linkId, codecTransport);

        // Persist the client's outgoing datagram sequence per host node so a restart resumes in step
        // with the host's expected-from-us (a fresh Flags1=0 is silently dropped as behind-sequence).
        string statePath = System.IO.Path.Combine(AppContext.BaseDirectory, "xmsg-client-sequence.state");
        FileResponderSequenceStore sequenceStore = new FileResponderSequenceStore(statePath);
        Console.WriteLine($"[client] datagram-sequence state file: {statePath}");

        // The asker: the shared link seed and our chosen client port, resuming its sequence from the store.
        RetroIO.Protocols.Xmsg.Node.Tad.TadAskerSession asker =
            new RetroIO.Protocols.Xmsg.Node.Tad.TadAskerSession(ownNode, hostNode, clientPort: 0x0283, seed, targetName, sequenceStore);
        asker.Log += line => Console.WriteLine(line);
        asker.TerminalText += text => Console.Write(text);   // render host output inline

        // Send a batch of frames down through the codec.
        void SendAll(System.Collections.Generic.IReadOnlyList<XmsgFrame> frames)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                codec.SendPacket(new XmsgPacket(frames[i]));
            }
        }

        // Host frame -> asker reacts -> send its response frames.
        codec.PacketReceived += delegate (string id, XmsgPacketInfo packet)
        {
            SendAll(asker.OnReceive(packet.Frame));
        };

        // Deliver link payloads up into the codec.
        adapter.PayloadReceived += delegate (ILink deliveringLink, byte[] payload, int length)
        {
            codec.ProcessBytes(payload.AsSpan(0, length));
        };

        // Fire the connect letter the first time the LAPB link becomes Active.
        bool connectSent = false;
        adapter.StatusChanged += delegate (ILink changedLink, LinkStatus oldStatus, LinkStatus newStatus, string reason)
        {
            Console.WriteLine($"[link] {changedLink.Name} {oldStatus} -> {newStatus} ({reason})");
            if (newStatus == LinkStatus.Active && !connectSent)
            {
                connectSent = true;
                SendAll(asker.Start());
            }
        };

        // Read stdin lines and send them as keystroke frames (best-effort; RFI gating is not enforced).
        Task inputTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                string? line = Console.ReadLine();
                if (line == null)
                {
                    break;
                }

                SendAll(asker.SendLine(line));
            }
        }, token);

        Console.WriteLine("[client] bringing up LAPB; type a line + Enter once the prompt appears.");
        adapter.Start();
        await adapter.Completion!;
        _ = inputTask;
    }

    /// <summary>
    /// The restructured seam composition: TcpBridgeTransport -> LapbLayerAdapter(ILink) ->
    /// BoundProtocolDetector -> XmsgCodec -> XmsgLayer. Routing/TAD services are configured on the
    /// layer; the link is bound to XMSG, and the detector (a per-link-binding stub) confirms it.
    /// </summary>
    /// <summary>
    /// Runs this node as a RELAY between two HDLC links: one the peer dials into, one we dial out.
    /// </summary>
    /// <param name="topology">
    /// The loaded topology, for the route table.
    /// </param>
    /// <param name="listenPort">
    /// The TCP port the inbound peer's bridge connects to.
    /// </param>
    /// <param name="dialHost">
    /// The host of the outbound bridge.
    /// </param>
    /// <param name="dialPort">
    /// The port of the outbound bridge.
    /// </param>
    /// <param name="node">
    /// The system number this runner presents on both links.
    /// </param>
    /// <param name="routingEntries">
    /// The routes this node advertises.
    /// </param>
    /// <param name="motdLine">
    /// The TAD banner middle line, or null for the built-in one.
    /// </param>
    /// <param name="users">
    /// The TAD login accounts.
    /// </param>
    /// <param name="fileServer">
    /// The COSMOS file server, or null when no folder is configured.
    /// </param>
    /// <param name="token">
    /// Stops the pump.
    /// </param>
    /// <returns>
    /// A task that completes when the pump stops.
    /// </returns>
    /// <remarks>
    /// <para><b>Why a node must LISTEN to be a relay</b></para>
    /// Every other path here dials out, which makes this node a leaf - an endpoint of somebody
    /// else's link. Traffic only crosses a relay when something is reachable ONLY through it, so at
    /// least one link has to be one the peer dials INTO.
    /// <para>
    /// Live shape: D103's <c>RetroCore.ini</c> points its HDLC at <paramref name="listenPort"/>
    /// instead of at D100, and we dial D100 ourselves. D103 traffic for D100 then has to cross this
    /// node, which is the only arrangement in which the relay carries anything.
    /// </para>
    /// <para><b>One host per link, deliberately</b></para>
    /// <see cref="XmsgNodeHost"/> is the whole per-link stack - codec, layer, responder sequence and
    /// learned link id all belong to ONE peer - so two links means two hosts, composed by
    /// <see cref="XmsgRelayNode"/>. That also sets each host's transit filter, which is what stops
    /// both a host and the relay acting on the same datagram.
    /// <para><b>Ordering matters</b></para>
    /// The listener comes up FIRST, so a peer already retrying its connect is accepted immediately.
    /// The outbound dial follows.
    /// </remarks>
    /// <summary>
    /// Reports whether an arriving subtype is a REQUEST that ought to be answered.
    /// </summary>
    /// <param name="subtype">
    /// The subtype of the arrived frame.
    /// </param>
    /// <returns>
    /// False for the subtypes that END an exchange, where producing no reply is correct.
    /// </returns>
    /// <remarks>
    /// <para>
    /// An acknowledgement, a reachability reply and a network error are all the far end of an
    /// exchange - answering one would be the fault. Without this the "no reply built, this hangs the
    /// caller" alarm fired on every healthy run, three times over, for arrivals that wanted nothing.
    /// </para>
    /// <para>
    /// It is shared by both dispatch handlers on purpose. They had drifted before: one printed a
    /// bare tag and the other a side-qualified one, and a rule kept in two places is a rule that
    /// will disagree with itself.
    /// </para>
    /// </remarks>
    private static bool ExpectsAReply(SintranPacketSubtype subtype)
    {
        switch (subtype)
        {
            case SintranPacketSubtype.Ack:
            case SintranPacketSubtype.ReachabilityReply:
            case SintranPacketSubtype.NetworkError:
                return false;

            default:
                return true;
        }
    }

    /// <summary>
    /// Builds the readiness test a transfer driver must satisfy before it originates.
    /// </summary>
    /// <param name="host">
    /// The node whose link knowledge decides readiness.
    /// </param>
    /// <param name="peer">
    /// The node a transfer will address.
    /// </param>
    /// <returns>
    /// A test that is true only once <paramref name="peer"/> has addressed us.
    /// </returns>
    /// <remarks>
    /// <para><b>Why this is a named function and not a lambda at the call site</b></para>
    /// <para>
    /// The gate was once written inline as "the LAPB link is Connected", which is true within
    /// milliseconds of startup. The sync daemon then originated before the peer had said anything,
    /// its connect letter was answered <c>XRUNN</c>, and because the driver discarded that answer it
    /// looked like silence. MEASURED 2026-08-17: with this gate instead, the same daemon carried two
    /// files to a real ND on the first attempt.
    /// </para>
    /// <para>
    /// <see cref="XmsgServerHost.CanReach"/> is true only once an INBOUND datagram has taught the
    /// layer the peer envelope seed - the point at which a frame we originate can be addressed at
    /// all. Naming it here means the rule is testable and has one definition rather than one per
    /// call site.
    /// </para>
    /// </remarks>
    /// <param name="originateFromSeed">
    /// <see langword="true"/> to allow originating to a peer we have met before using its remembered
    /// envelope seed, instead of waiting for it to address us again. Opt-in; see the remarks.
    /// </param>
    /// <remarks>
    /// <para><b>The seed gate is an EXPERIMENT and is off by default</b></para>
    /// <para>
    /// With <paramref name="originateFromSeed"/> set, the gate becomes
    /// <see cref="XmsgServerHost.OpenLinkFromRememberedSeed"/>, which succeeds for any peer whose
    /// seed is in the store. That is what a daemon needs to work from cold - without it every
    /// transfer waits for a person to type a command on the far machine.
    /// </para>
    /// <para>
    /// It is opt-in because the last measurement went against it: D100 answered <c>XRUNN</c> to a
    /// letter sent from the remembered seed alone. That test predates both the access grant and the
    /// discovery that the outgoing counter was being zeroed on every run that day, so it is worth
    /// repeating - but not worth making the default until it has been.
    /// </para>
    /// </remarks>
    public static Func<bool> BuildSyncReadiness(XmsgNodeHost host, ushort peer, bool originateFromSeed)
    {
        if (host == null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (originateFromSeed)
        {
            return () => host.ServerHost.OpenLinkFromRememberedSeed(peer);
        }

        return () => host.ServerHost.CanReach(peer);
    }

    private static async Task RunRelayAsync(
        TopologyConfig? topology, int listenPort, string dialHost, int dialPort, ushort node,
        ushort inboundPeer, ushort outboundPeer,
        IReadOnlyList<RoutingTableEntry> routingEntries, string? motdLine, TadUserDirectory users,
        FaServer? fileServer, CancellationToken token)
    {
        Console.WriteLine($"[relay] listening on {listenPort} for the inbound bridge...");

        // Listener first: the peer may already be retrying, and this accepts it at once.
        TcpBridgeTransport inboundTransport = await TcpBridgeTransport.ListenAsync(listenPort, token);
        Console.WriteLine($"[relay] inbound bridge connected on {listenPort}");

        TcpBridgeTransport outboundTransport = await TcpBridgeTransport.ConnectAsync(dialHost, dialPort, token);
        Console.WriteLine($"[relay] outbound bridge connected to {dialHost}:{dialPort}");

        LapbLayer inboundLink = new LapbLayer(node);
        LapbLayerAdapter inbound = new LapbLayerAdapter($"hdlc-in:{listenPort}", inboundTransport, inboundLink);

        LapbLayer outboundLink = new LapbLayer(node);
        LapbLayerAdapter outbound = new LapbLayerAdapter($"hdlc-out:{dialHost}:{dialPort}", outboundTransport, outboundLink);

        string statePath = System.IO.Path.Combine(AppContext.BaseDirectory, "xmsg-sequence.state");
        FileResponderSequenceStore sequenceStore = new FileResponderSequenceStore(statePath);

        RetroIO.Protocols.Xmsg.Servers.Tad.TadServer tadServer =
            new RetroIO.Protocols.Xmsg.Servers.Tad.TadServer(() => DateTime.Now, users: users, motdLine: motdLine);

        // BOTH links serve. See BuildRelayHosts - giving the servers to only one of them is a
        // measured defect, not a theoretical one.
        XmsgNodeHost[] hosts = BuildRelayHosts(
            inbound, outbound, node, routingEntries, sequenceStore,
            BuildServerList(tadServer, fileServer));
        XmsgNodeHost inboundHost = hosts[0];
        XmsgNodeHost outboundHost = hosts[1];

        // The host's own log channel was never connected to anything, so every line it writes has
        // been going nowhere - including its "frame implies seed X but link seed is Y" WARNING,
        // which is meant to flag exactly the out-of-model frames worth investigating. Found while
        // instrumenting the Flags 1 sequence for task #28: the new [seq] lines printed nothing
        // until this was wired.
        inboundHost.ServerHost.Log = line => Console.WriteLine("[in  " + line);
        outboundHost.ServerHost.Log = line => Console.WriteLine("[out " + line);

        tadServer.ServerDirectory = inboundHost.ServerHost.DescribeServers;
        tadServer.RouteReport = inboundHost.FormatRouteReport;

        // Routes read off the topology. BOTH links need them: registering only the outbound side
        // would leave traffic coming the other way - from the dialled peer towards the inbound one -
        // with no route, and the relay would drop it as unroutable rather than forward it.
        //
        // A link reaches its own peer, plus every node whose "via" chain STARTS at that peer. Only
        // the first hop of a chain decides which of OUR links to use; the rest is the next relay's
        // problem, which is how the captured route-through behaves.
        XmsgRelayNode relay = new XmsgRelayNode();

        // A DIRECT peer of one link is never reachable "through" the other, whatever the topology
        // file says. Caught on the first live run: topology-d19999.json still declares D103 as
        // "via": [100] from when D103 hung off D100, so 103 landed on BOTH links - and because
        // AddLink re-points a duplicate route at whichever link registered last, every datagram for
        // D103 would have gone out of the D100 link instead of down the link D103 is actually on.
        // A silent misroute, and exactly the failure the topology tests were written for.
        ushort[] throughInbound = RoutesThrough(topology, inboundPeer, outboundPeer);
        ushort[] throughOutbound = RoutesThrough(topology, outboundPeer, inboundPeer);

        relay.AddHost(inboundHost, throughInbound);
        relay.AddHost(outboundHost, throughOutbound);

        Console.WriteLine(
            $"[relay] node {node}: inbound link on {listenPort} reaches [{string.Join(",", throughInbound)}], "
            + $"outbound to {dialHost}:{dialPort} reaches [{string.Join(",", throughOutbound)}]");

        // Link state on BOTH links, and the reachability ANNOUNCE when each comes up.
        //
        // Without the announce a peer never registers us at the XMSG level: the LAPB link goes
        // Active, its routing table still says it can reach us, and yet every request answers
        // "Remote system is not accessible". Measured on D103 - link Active, route "*->19999",
        // still unreachable - because nothing had ever told its XROUT we were here. The
        // single-link path announces on link-up; this one did not, which is why no traffic could
        // ever cross the relay.
        //
        // A relay must announce on BOTH links. Announcing only outbound leaves the inbound peer
        // unable to reach anything at all, including us.
        // EVERY time a link reaches Active, not just the first. A once-only guard looks tidy and is
        // wrong: when this process restarts, both peers re-SABM and the links bounce
        // Active -> Starting -> Active. With a one-shot flag the re-establish gets no announce, so a
        // peer that dropped us stays unable to reach us and nothing ever tells it otherwise -
        // exactly the "no access to system 19999" state, and invisible because the link looks fine.
        //
        // Re-announcing is safe: the announce resets our own sequence for that peer to match, which
        // is what it is for.
        // ARM here, SEND on the next loop tick - never from inside the status callback.
        //
        // Announcing directly from StatusChanged is what made this relay unusable, and it was
        // captured on 2026-08-08 (DOC/captures/FRMR-ON-INNAK-2026-08-08). The callback fires while
        // the layer is still working through a BATCH of received frames, and D100's queued SABM
        // retries arrive as SIX SABMs in a single TCP segment. Answer the first, announce from the
        // callback so V(S) becomes 1, then answer the remaining five - and spec 3.2 hard-zeroes
        // V(S) on every one of them. D100, which never reset its view of our announce, then
        // acknowledges it with N(R)=1, that no longer fits the window from V(A) to V(S), and spec
        // 4.3 obliges us to answer FRMR reason Z. The peer retransmits forever and nothing crosses
        // the relay.
        //
        // Both rules are MUST and both are correct; the defect was transmitting BETWEEN two resets.
        // Deferring to the loop tick means the whole burst is drained first. The single-link path
        // above already did exactly this ("sent here because sending from inside that callback
        // re-enters the adapter and drops the link") - the relay path did not, and that was the
        // entire difference. Pinned by LapbAnnounceOrderingTests, which replays the captured bytes.
        //
        // The flags are re-armed on EVERY Active, not once: a restart bounces both links, and a
        // one-shot guard would leave a peer that dropped us permanently unable to reach us.
        // onceOnly: false - a relay MUST re-announce on every Active. See LinkAnnouncer, which owns
        // the arm-then-send-on-tick mechanism for both this path and the single-link one.
        LinkAnnouncer inboundAnnouncer = new LinkAnnouncer(
            () => inbound.Status,
            () =>
            {
                Console.WriteLine($"[relay] announcing to inbound peer {inboundPeer}");
                inboundHost.AnnounceRestart(inboundPeer);
            },
            enabled: true,
            onceOnly: false);

        LinkAnnouncer outboundAnnouncer = new LinkAnnouncer(
            () => outbound.Status,
            () =>
            {
                Console.WriteLine($"[relay] announcing to outbound peer {outboundPeer}");
                outboundHost.AnnounceRestart(outboundPeer);
            },
            enabled: true,
            onceOnly: false);

        inbound.StatusChanged += (link, previous, current, reason) =>
        {
            Console.WriteLine($"[relay] inbound link {previous} -> {current} ({reason})");
            inboundAnnouncer.OnStatusChanged(current);
        };

        outbound.StatusChanged += (link, previous, current, reason) =>
        {
            Console.WriteLine($"[relay] outbound link {previous} -> {current} ({reason})");
            outboundAnnouncer.OnStatusChanged(current);
        };

        // What each peer SENDS US, and whether we answered it.
        //
        // The relay path had neither, which made a hang invisible: a request addressed to our own
        // node is not transit, so the relay counters stay still and the log says nothing while the
        // caller waits out its timeout. The single-link path carries this and the ANSWERED-OR-NOT
        // check below for exactly that reason - an inbound datagram that produces no reply hangs
        // the calling SINTRAN terminal, and ESC will not abort it.
        LogReceivedOn("in ", inboundHost);
        LogReceivedOn("out", outboundHost);

        relay.Relay.Relayed += (fromLink, toLink, destination) =>
            Console.WriteLine($"[relay] {fromLink.Name} -> {toLink.Name} for node {destination}");
        relay.Relay.NotRelayed += (fromLink, destination, reason) =>
            Console.WriteLine($"[relay] DROPPED from {fromLink.Name} for node {destination}: {reason}");

        // Each adapter runs its own read/timer loop and raises LoopTick per iteration, so the relay
        // is pumped from whichever link is active rather than from a third loop of our own.
        long lastRelayed = 0;
        LapbLayerAdapter.LinkLoopTick pump = delegate
        {
            relay.Pump();

            if (relay.Relay.DatagramsRelayed != lastRelayed)
            {
                lastRelayed = relay.Relay.DatagramsRelayed;
                Console.WriteLine(
                    $"[relay] relayed={lastRelayed} forUs={relay.Relay.DatagramsForUs} "
                    + $"dropped={relay.Relay.DatagramsDropped}");
            }
        };

        // The deferred announces, one handler per link so each fires on its OWN loop thread and
        // checks its OWN status. By the time a loop tick runs, every frame that arrived in the last
        // batch has been processed - including a whole burst of SABMs - so V(S) is settled and the
        // announce cannot land between two resets. See the StatusChanged comment above.
        inbound.LoopTick += delegate { inboundAnnouncer.OnLoopTick(); };
        outbound.LoopTick += delegate { outboundAnnouncer.OnLoopTick(); };

        inbound.LoopTick += pump;
        outbound.LoopTick += pump;

        inbound.Initiate();
        outbound.Initiate();
        Console.WriteLine("[relay] SABM sent on both links; pumping...");

        // BOTH links need a keepalive interval, and running without one was a second defect found on
        // 2026-08-08 while verifying the announce fix. With keepaliveInterval null the adapter
        // documents itself as "the pump simply blocks on reads (in-memory tests)": LoopTick then
        // fires ONLY when a frame arrives, so the deferred announce could sit unsent forever, and -
        // worse - the LAPB T1 retransmit and T3 keepalive timers never tick at all on a LIVE link.
        // The single-link path has always passed 20 ms; the relay path never did.
        await Task.WhenAll(
            inbound.RunAsync(token, TimeSpan.FromMilliseconds(20)),
            outbound.RunAsync(token, TimeSpan.FromMilliseconds(20)));
    }

    /// <summary>
    /// Logs what a host receives, and whether it managed to answer.
    /// </summary>
    /// <param name="side">
    /// A short label naming which link this host sits on, for the log line.
    /// </param>
    /// <param name="host">
    /// The host to watch.
    /// </param>
    /// <remarks>
    /// The second handler is the one that matters. A request that produces NO reply leaves the
    /// calling SINTRAN waiting out its timeout with ESC unable to abort it, and until this was
    /// logged the two cases looked identical from here - silence either way.
    /// </remarks>
    private static void LogReceivedOn(string side, XmsgNodeHost host)
    {
        host.Layer.MessageReceived += delegate (string id, XmsgPacketInfo packet)
        {
            XmsgFrame f = packet.Frame;
            Console.WriteLine(
                $"[{side} RX] {f.Header.SourceNode}->{f.Header.DestinationNode} "
                + $"sub={f.Header.Subtype} f1=0x{f.Header.Flags1:X4} "
                + $"info={Convert.ToHexString(packet.RawBytes)}");
        };

        host.Layer.DispatchCompleted += delegate (string id, XmsgPacketInfo packet, int produced)
        {
            XmsgFrame f = packet.Frame;

            if (produced == 0)
            {
                // Same rule as the ethernet seam: an Ack, a reachability reply and a network error
                // END an exchange, so no reply is the correct outcome and the alarm must not fire.
                if (!ExpectsAReply(f.Header.Subtype))
                {
                    Console.WriteLine(
                        $"[{side} RX] {f.Header.SourceNode}->{f.Header.DestinationNode} "
                        + $"sub={f.Header.Subtype} (no reply expected)");
                    return;
                }

                Console.WriteLine(
                    $"[{side} RX] {f.Header.SourceNode}->{f.Header.DestinationNode} "
                    + $"sub={f.Header.Subtype} *** NO REPLY BUILT *** (this hangs the caller)");
                return;
            }

            Console.WriteLine(
                $"[{side} RX] {f.Header.SourceNode}->{f.Header.DestinationNode} "
                + $"sub={f.Header.Subtype} answered with {produced} frame(s)");
        };
    }

    /// <summary>
    /// Gets the node numbers a link reaches: its own peer, plus every node routed through that peer.
    /// </summary>
    /// <param name="topology">
    /// The loaded topology, or null when none was supplied.
    /// </param>
    /// <param name="peer">
    /// The node on the far end of the link.
    /// </param>
    /// <param name="otherLinkPeer">
    /// The node on the far end of this relay's OTHER link, which is excluded.
    /// </param>
    /// <returns>
    /// The reachable node numbers, always including <paramref name="peer"/> itself.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The peer is included because a relay must be able to deliver TO the machine on the other end
    /// of a link, not only through it. Leaving it out is a silent way to make a directly connected
    /// node unreachable.
    /// </para>
    /// <para><b>Why the other link's peer is excluded</b></para>
    /// A topology file describes how nodes were reachable when it was written. Moving a machine onto
    /// a relay's own link does not update it, so a node that is now a DIRECT neighbour can still be
    /// declared "via" somewhere else - and it would then be registered on both links, with the
    /// second registration winning. Excluding the other link's peer makes the direct connection
    /// authoritative over a stale route, which is the safe direction to be wrong in.
    /// </remarks>
    private static ushort[] RoutesThrough(TopologyConfig? topology, ushort peer, ushort otherLinkPeer)
    {
        List<ushort> routes = new List<ushort>();
        routes.Add(peer);

        if (topology != null)
        {
            List<ushort> via = topology.NodesReachableThrough(peer);
            for (int i = 0; i < via.Count; i++)
            {
                if (via[i] != otherLinkPeer && !routes.Contains(via[i]))
                {
                    routes.Add(via[i]);
                }
            }
        }

        return routes.ToArray();
    }

    /// <summary>
    /// Turns the <c>--chat-room</c> option into the list of rooms to spread users over.
    /// </summary>
    /// <param name="option">
    /// The option's value, or null when it was not given.
    /// </param>
    /// <returns>
    /// One or more room names, upper-cased. Defaults to LOBBY alone.
    /// </returns>
    /// <remarks>
    /// Upper-cased here rather than left to the server, so the run's own arithmetic in
    /// <c>ChatLoadRun.ExpectedSaid</c> groups by the same spelling the server files people under.
    /// Lower-cased input would otherwise count two rooms where the server keeps one.
    /// </remarks>
    private static string[] SplitRooms(string? option)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return new string[] { "LOBBY" };
        }

        string[] parts = option.Split(',');
        List<string> rooms = new List<string>();
        for (int i = 0; i < parts.Length; i++)
        {
            string name = parts[i].Trim().ToUpperInvariant();
            if (name.Length > 0)
            {
                rooms.Add(name);
            }
        }

        if (rooms.Count == 0)
        {
            return new string[] { "LOBBY" };
        }

        return rooms.ToArray();
    }

    private static async Task RunSeamAsync(
        TcpBridgeTransport transport, string host, int port, ushort node,
        IReadOnlyList<RoutingTableEntry> routingEntries, string? motdLine, TadUserDirectory users,
        FaServer? fileServer, FaPushRun? pushRun, FaPullRun? pullRun,
        bool announceRestart, bool resyncHard, bool originateFromSeed,
        string? syncFolder, string? syncPullFolder, string syncMachine, string syncUser, ushort syncNode,
        string? batchInput, string? batchOutput, string batchMachine, ushort batchNode,
        int chatLoadUsers, string chatServiceName, string[] chatRoomNames, ushort chatNode, string chatMachine, int chatLines,
        CancellationToken token)
    {
        string linkId = $"hdlc:{host}:{port}";

        // APPEND-REMOTE-BATCH on this path too.
        //
        // It was wired to the Ethernet seam alone and REFUSED here, which was honest but wrong: an
        // XROUT letter to *XFTRA has nothing Ethernet-specific about it, and the refusal was
        // missing plumbing rather than a protocol limit. That makes four flags found wired to the
        // one path - --announce-restart with --resync-hard, the link-seed store, --sync, and now
        // this. When a flag is refused "because of the transport", check whether the transport
        // actually cares.
        AppendRemoteBatchRun? batchRun = null;

        // The live chat load run rides this seam too, for the same reason the batch letter does:
        // an XSLET letter to a named room has nothing transport-specific about it.
        ChatLoadRun? chatLoadRun = null;
        if (chatLoadUsers > 0)
        {
            chatLoadRun = new ChatLoadRun(
                chatNode, chatMachine, chatServiceName, chatRoomNames, chatLoadUsers, chatLines);
        }

        if (batchInput != null)
        {
            batchRun = new AppendRemoteBatchRun(
                batchNode, batchMachine, "SYSTEM", batchInput, batchOutput ?? "ARBOUT:SYMB");
        }

        LapbLayer link = new LapbLayer(node);
        LapbLayerAdapter adapter = new LapbLayerAdapter(linkId, transport, link);

        // Persist our outgoing datagram sequence per remote node across restarts (a state file next
        // to the runner), so we continue in step with 100's persistent expected-from-us instead of
        // resetting to 0x0000 and being silently dropped. See XMSG-SEQUENCE-RESTART-ANSWER doc.
        string statePath = System.IO.Path.Combine(AppContext.BaseDirectory, "xmsg-sequence.state");
        FileResponderSequenceStore sequenceStore = new FileResponderSequenceStore(statePath);
        Console.WriteLine($"[runner] datagram-sequence state file: {statePath}");

        // The MOTD banner middle line comes from the topology file when set; otherwise the server uses its
        // built-in "Emulated TAD server version vN.N.N". The host-id line is generated from our own node.
        RetroIO.Protocols.Xmsg.Servers.Tad.TadServer tadServer =
            new RetroIO.Protocols.Xmsg.Servers.Tad.TadServer(() => DateTime.Now, users: users, motdLine: motdLine);
        Console.WriteLine($"[tad] {users.Count} login account(s) loaded");
        tadServer.SessionOpened += (tadNumber, clientSystem) =>
            Console.WriteLine($"[tad] session opened: tty{tadNumber} from node {clientSystem}");
        tadServer.SessionClosed += (tadNumber, clientSystem) =>
            Console.WriteLine($"[tad] session closed: tty{tadNumber} from node {clientSystem}");

        // Everything above the link - detector, codec, XmsgLayer, routing table, XmsgServerHost and
        // the registered servers - is transport-agnostic and lives in XmsgNodeHost, so the Ethernet
        // node reuses this composition instead of copying it. Only the LAPB diagnostics below are
        // HDLC-specific.
        XmsgNodeHost nodeHost = new XmsgNodeHost(
            adapter, node, routingEntries, sequenceStore, BuildServerList(tadServer, fileServer));
        XmsgLayer layer = nodeHost.Layer;

        // PLUG THE SERVER-HOST LOG IN ON THIS PATH TOO.
        //
        // It was wired on the relay and Ethernet paths and NOT here, so over the HDLC bridge every
        // [seq] line the host emits was thrown away - including "link opened from the REMEMBERED
        // seed", which is the one line that says whether a transfer originated from the store or
        // waited to be spoken to. Measuring the cold-start question without it took an extra
        // control run and a timestamp comparison to answer something the program already knew.
        // Same lesson as the two diagnostics found on 2026-08-11: a diagnostic that is not plugged
        // in is not a diagnostic.
        nodeHost.ServerHost.Log = line => Console.WriteLine(line);

        // Wire the introspection commands: "list servers" reads the host's registered servers; "list route"
        // formats the routing table this node advertises.
        tadServer.ServerDirectory = nodeHost.ServerHost.DescribeServers;
        tadServer.RouteReport = nodeHost.FormatRouteReport;
        // REMEMBER THE ENVELOPE SEED ON THIS PATH TOO.
        //
        // This was wired on the ETHERNET seam only, so over the HDLC bridge the store was never
        // attached, OpenLinkFromRememberedSeed always returned false, and the seed file sat on disk
        // unused. The same shape as --announce-restart being dropped here: a capability that exists,
        // is persisted, and is silently never consulted.
        //
        // The cost was not only the daemon case. It made an experiment impossible: a transfer could
        // only start once the peer had spoken to us, so every push had to be preceded by a console
        // command, and a push against a genuinely IDLE D100 could never be observed.
        // THE FOLDER-WATCH DAEMON, on this path at last. It was wired to the Ethernet seam only, so
        // it had never run over HDLC at all - see the refusal in Main that used to be the only thing
        // standing between an operator and a runner that silently never synced.
        //
        // Readiness here is the LAPB link being Connected. On the Ethernet seam the equivalent gate
        // is HasLearnedPeer; both mean "a frame we originate can actually be addressed".
        SyncDaemon? syncDaemon = null;
        if (syncFolder != null)
        {
            syncDaemon = new SyncDaemon(
                syncFolder,
                syncMachine,
                syncUser,
                nodeHost,
                syncNode,
                BuildSyncReadiness(nodeHost, syncNode, originateFromSeed),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(5),
                syncPullFolder);

            if (syncUser == SyncDaemon.RootMarker)
            {
                Console.WriteLine(
                    $"[sync] watching ROOT {syncFolder} -> node {syncNode} ({syncMachine}),"
                    + " one folder per SINTRAN user");
            }
            else
            {
                Console.WriteLine(
                    $"[sync] watching {syncFolder} -> node {syncNode} ({syncMachine}) user {syncUser}");
            }

            if (originateFromSeed)
            {
                Console.WriteLine(
                    "[sync] --originate-from-seed: transfers may start as soon as node "
                    + $"{syncNode}'s seed is found in the store, WITHOUT waiting for it to address"
                    + " us. This is the experiment - if the peer answers XRUNN, the gate was right.");
            }
        }

        string hdlcSeedPath = System.IO.Path.Combine(AppContext.BaseDirectory, "xmsg-link-seed.state");
        nodeHost.ServerHost.SeedStore = new FileLinkSeedStore(hdlcSeedPath);
        Console.WriteLine($"[runner] link-seed state file: {hdlcSeedPath}");

        // SABM-storm suppression. A short burst of peer SABMs during link establishment is NORMAL (the
        // balanced SABM/UA handshake) - it is NOT "machine down". We collapse the repeated SABM churn
        // (and our UA/RR echoes) quietly, and ONLY escalate to a "peer XMSG is down" warning if the
        // SABMs PERSIST with no data phase for several seconds (a crashed peer re-SABMs forever).
        bool stormActive = false;
        bool peerDownWarned = false;
        int peerSabmStreak = 0;
        DateTime sabmStreakStart = default;
        TimeSpan sabmDownThreshold = TimeSpan.FromSeconds(5);

        // Diagnostics: log every LAPB body we transmit (link.OnTransmit is multicast, so this fires
        // alongside the adapter's own encode-and-queue handler).
        link.OnTransmit += body =>
        {
            byte ctrl = body.Length > 1 ? body[1] : (byte)0;
            bool isData = (ctrl & 1) == 0;
            // During a peer SABM storm our UA/RR answers are pure churn - suppress them; data frames
            // (I-frames) are always logged.
            if (stormActive && !isData)
            {
                return;
            }

            string kind;
            if (isData) kind = $"I ns={(ctrl >> 1) & 7} nr={(ctrl >> 5) & 7}";
            else if ((ctrl & 3) == 1) kind = $"RR nr={(ctrl >> 5) & 7}";
            else kind = ctrl switch { 0x3F => "SABM", 0x73 => "UA", _ => $"U 0x{ctrl:X2}" };
            string extra = isData ? $" body={Convert.ToHexString(body)}" : string.Empty;

            // A FRMR WITHOUT ITS DIAGNOSTIC IS UNDEBUGGABLE. The frame carries three bytes saying
            // WHICH control byte was rejected, our V(S)/V(R) at the time, and WHY (W = unimplemented
            // control, X = I-field not permitted, Y = I-field too long, Z = invalid N(R)) - and the
            // log threw all three away, leaving only "U 0x87". A whole round of reasoning went into
            // guessing which of the three FRMR call sites had fired, when the answer was on the wire.
            // Spec 2.3.3. Body is address, control, then the three diagnostic bytes.
            if (!isData && (ctrl & 0xEF) == 0x87 && body.Length >= 5)
            {
                byte reason = body[4];
                string why = string.Empty;
                if ((reason & 0x01) != 0) { why += "W(unimplemented control) "; }
                if ((reason & 0x02) != 0) { why += "X(I-field not permitted) "; }
                if ((reason & 0x04) != 0) { why += "Y(I-field too long) "; }
                if ((reason & 0x08) != 0) { why += "Z(invalid N(R)) "; }
                if (why.Length == 0) { why = "(no reason bit set) "; }
                extra = $" FRMR rejected-control=0x{body[2]:X2}"
                      + $" V(S)={(body[3] >> 1) & 7} V(R)={(body[3] >> 5) & 7}"
                      + $" reason=0x{reason:X2} {why}";
            }
            // V(S)/V(A) ON EVERY FRAME. The FRMR diagnostic said V(S)=0 immediately after we had
            // transmitted I ns=0, which cannot both be true - so the variables themselves have to be
            // on the record, not inferred from what was sent.
            Console.WriteLine($"[TX] a=0x{(body.Length > 0 ? body[0] : 0):X2} {kind} state={link.State}"
                + $" vs={link.SendVariable} va={link.AcknowledgeVariable} vr={link.ReceiveVariable}{extra}");
        };

        // Diagnostics: log every decoded SINTRAN frame the layer receives, the same way the legacy
        // runner did (full XMSG sub-header + TAD chain for data frames).
        layer.MessageReceived += delegate (string id, XmsgPacketInfo packet)
        {
            XmsgFrame f = packet.Frame;
            Console.WriteLine(
                $"[RX] {f.Header.SourceNode}->{f.Header.DestinationNode} " +
                // Offsets 12-13 are the header checksum, not a protocol selector - print the whole
                // word rather than its high byte under a name that invented a field.
                $"sub={f.Header.Subtype} cksum=0x{f.Header.Checksum:X4} f1=0x{f.Header.Flags1:X4} " +
                $"info={Convert.ToHexString(packet.RawBytes)}");
            if (f.Header.Subtype == SintranPacketSubtype.Data && f.SubHeader != null)
            {
                ushort dp = f.SubHeader.DestinationPort;
                ushort sp = f.SubHeader.SourcePort;
                Console.WriteLine(
                    $"      ports: src {f.SubHeader.SourceSystem}:{sp} (log {sp >> 7}/low {sp & 0x7F})" +
                    $" -> dst {f.SubHeader.DestinationSystem}:{dp} (log {dp >> 7}/low {dp & 0x7F})" +
                    $"  XMCSM=0x{f.XmcsmWithBodyWord:X8} role=0x{f.SubHeader.Role:X2}");
                Console.Write(RetroIO.Protocols.Xmsg.Diagnostics.XmsgDump.ToText(f));
            }

            // A push is a conversation we started, so its replies arrive here like anything else.
            // The driver decides which frames are its own - a TAD session on the same link must not
            // be able to fail a file transfer.
            if (pushRun != null)
            {
                pushRun.OnFrame(f);
            }

            // The daemon's own transfer replies arrive here like anything else.
            syncDaemon?.OnFrame(f);

            // A pull is the same kind of conversation, and its content arrives the same way. The
            // frames have already been through the fragment reassembler by this point, so a
            // 1032-byte data message arrives whole rather than as its 0x0A and 0x0C halves.
            if (pullRun != null)
            {
                pullRun.OnFrame(f);
            }

            if (batchRun != null)
            {
                batchRun.OnFrame(f);
            }

            chatLoadRun?.OnFrame(f);
        };

        layer.SessionOpened += delegate (string id, ushort clientSystem, ushort clientPort)
        {
            Console.WriteLine($"[session] opened by system {clientSystem} port 0x{clientPort:X4}");
        };
        // Announce ourselves ONCE, the first time the link comes up: our XMSG has just started, so
        // the peer must reset the datagram sequence it expects from us. Without this the peer keeps
        // counting from wherever it was while we start at zero, and the conversation dies silently
        // just after the connect confirmation. See XmsgNodeHost.AnnounceRestart.
        // OFF by default - it does net HARM as it stands. MEASURED 2026-08-04, with the header
        // checksum already fixed and the send already deferred to the loop tick:
        //
        //     20:40:19.625  link Starting -> Active
        //     20:40:19.667  announce sent
        //     20:40:20.027  link Active -> Starting        <- the link goes down anyway
        //     20:40:34.724  link back to Active
        //
        // and afterwards the two sides were still out of step - D100 opened its conversation at
        // Flags 1 0x000C while we were at 0x0000 - so the announce did not achieve the one thing it
        // exists for. Either D100 re-establishes LAPB when told a peer restarted (arguably correct)
        // and the request is lost in the reset, or it never processed it at all. UNKNOWN which.
        //
        // Without it, both sides simply start from zero after a machine restart, which is what the
        // runs that DID work today all had. Flip this to true only to investigate further.
        //
        // NOTE this measurement is HDLC/LAPB only. The Ethernet path has its own decision - see
        // the --announce-restart option in RunEthernetSeamAsync - because the harm described above
        // is a LAPB re-establishment, and Ethernet has no LAPB.
        const bool AnnounceRestartOnLinkUp = false;

        // ...but --announce-restart MUST be able to turn it on, and until 2026-08-17 it could not:
        // the flag was parsed, handed to the ETHERNET seam only, and dropped on the floor here. Over
        // the HDLC bridge it did nothing at all and said nothing about doing nothing. MEASURED that
        // day: D100 answered "UNKNOWN REMOTE SYSTEM NAME" for D19999 - it had never been told we
        // exist - while the runner cheerfully reported an Active link and a file server ready to
        // serve. Every "cured by --resync-hard" claim made over this path is therefore about some
        // OTHER change; the announce was never sent.
        //
        // The default stays false for the reason documented above. The flag is the opt-in.
        bool announceOnLinkUp = AnnounceRestartOnLinkUp || announceRestart;

        // Arm on status, send on the next loop tick - the mechanism lives in LinkAnnouncer, shared
        // with the relay path. Sending from inside the status callback re-enters the LAPB adapter
        // while it is still completing the transition and drops the link (measured 260 ms after the
        // announce, 2026-08-04); on the relay it produced an FRMR storm instead. One copy of that
        // rule, two policies.
        //
        // onceOnly: true here - unlike the relay, this path announced at most once when it was
        // enabled at all.
        LinkAnnouncer announcer = new LinkAnnouncer(
            () => adapter.Status,
            () =>
            {
                for (int i = 0; i < routingEntries.Count; i++)
                {
                    RoutingTableEntry entry = routingEntries[i];
                    // Direct neighbours only (Hops == 1). Ourselves, and anything reached THROUGH
                    // another node, are not ours to announce to.
                    if (entry.System == node || entry.Hops != 1)
                    {
                        continue;
                    }

                    Console.WriteLine(
                        $"[link] announcing our XMSG restart to node {entry.System} " +
                        "(ReachabilityRequest; resets both sides' Flags 1 to 0x0000)");

                    if (resyncHard)
                    {
                        // Zero OUR counter as well. D103 proved this wrong over HDLC - it does not
                        // reset its expected-from-us on our announce, so zeroing ours puts every
                        // later frame BEHIND-sequence, where it is dropped in silence.
                        //
                        // MEASURED 2026-08-17: D100 does NOT reset either, and the earlier claim that
                        // it did was wrong. D100 publishes the pair in X-C LIST-SYSTEMS ("Sequence no.
                        // Send-receive"); across an --announce-restart AND a --request-link it stayed
                        // at send 64 / receive 65 and ignored the 0x0000 frames that followed. Running
                        // WITHOUT this flag, so the stored counter carried on from 0x0041, pulled a
                        // 20400-byte file first time and moved the peer's column to 124 - matching our
                        // stored 0x007C exactly. So this flag does not recover a drifted sequence
                        // against D100; it CREATES one. Prefer the persisted counter.
                        // Say WHAT is being discarded, not just that something is. A run that throws
                        // away a stored 0x0041 and one that was already at 0x0000 look identical in
                        // the log without this, and telling those two apart is exactly what was
                        // needed to explain why a second process gets no answer: per
                        // IResponderSequenceStore, resetting to 0x0000 while the peer's XSRSQ has
                        // advanced puts every later frame BEHIND-sequence, where it is dropped in
                        // silence.
                        ushort storedFlags1 = sequenceStore.LoadNextFlags1(entry.System);
                        Console.WriteLine(
                            $"[announce] --resync-hard: also zeroing OUR counter for {entry.System}."
                            + $" Discarding the stored next-Flags1 0x{storedFlags1:X4}."
                            + " The next originated frame goes out at 0x0000 - if the peer accepts"
                            + " it, the peer resets on our announce; if it vanishes, it does not.");
                        if (storedFlags1 != 0)
                        {
                            Console.WriteLine(
                                $"[announce] WARNING: node {entry.System} was at 0x{storedFlags1:X4}, not a fresh"
                                + " contact. If the peer does NOT reset on our announce, every frame"
                                + " from here is behind-sequence and will be dropped without an error.");
                        }

                        nodeHost.AnnounceRestartAndResetOurs(entry.System);
                    }
                    else
                    {
                        nodeHost.AnnounceRestart(entry.System);
                    }
                }
            },
            enabled: announceOnLinkUp,
            onceOnly: true);

        adapter.StatusChanged += delegate (ILink changedLink, LinkStatus oldStatus, LinkStatus newStatus, string reason)
        {
            Console.WriteLine($"[link] {changedLink.Name} status {oldStatus} -> {newStatus} ({reason})");
            announcer.OnStatusChanged(newStatus);
        };

        // Diagnostic: log every raw LAPB frame 100 sends us (SABM/UA/RR/I). Detects and COLLAPSES a
        // bare-link SABM storm (peer XMSG crashed) into a single actionable warning.
        adapter.RawFrameReceived += delegate (string id, byte[] body)
        {
            byte ctrl = body.Length > 1 ? body[1] : (byte)0;

            if (ctrl == 0x3F)   // SABM from the peer
            {
                peerSabmStreak++;
                if (peerSabmStreak == 1)
                {
                    sabmStreakStart = DateTime.UtcNow;
                }

                // Collapse the repeated SABM churn after a few. This is NORMAL link-establishment
                // handshaking - do NOT claim the peer is down for it.
                if (peerSabmStreak >= 3 && !stormActive)
                {
                    stormActive = true;
                    Console.WriteLine("[link] collapsing repeated peer SABMs (link establishing)...");
                }

                // Only if the SABMs PERSIST with no data phase for several seconds is the peer's XMSG
                // actually down (a crashed peer re-SABMs a bare link forever). Say so once, accurately.
                if (stormActive && !peerDownWarned
                    && DateTime.UtcNow - sabmStreakStart > sabmDownThreshold)
                {
                    peerDownWarned = true;
                    Console.WriteLine(
                        "[!] Peer has re-sent SABM for over 5s with no XMSG data - machine 100's XMSG is " +
                        "likely DOWN (crashed / not started). Restart XMSG on 100, then reconnect.");
                }

                if (stormActive)
                {
                    return;   // suppress the repeated SABM lines
                }
            }
            else
            {
                if (stormActive)
                {
                    // Only note "resumed" if we had actually warned the peer was down; a normal
                    // establishment burst resolves silently.
                    if (peerDownWarned)
                    {
                        Console.WriteLine("[link] peer traffic resumed.");
                    }

                    stormActive = false;
                    peerDownWarned = false;
                }

                peerSabmStreak = 0;
            }

            string kind;
            if ((ctrl & 1) == 0)
            {
                kind = $"I ns={(ctrl >> 1) & 7} nr={(ctrl >> 5) & 7}";
            }
            else if ((ctrl & 3) == 1)
            {
                // S-frame: the supervisory subtype is bits 2-3 (0=RR, 1=RNR, 2=REJ). Do NOT label every
                // S-frame "RR" - that hid 100's REJ (ctrl 0xC9) as an "RR" in the raw log.
                string s = ((ctrl >> 2) & 3) switch { 0 => "RR", 1 => "RNR", 2 => "REJ", _ => "S?" };
                kind = $"{s} nr={(ctrl >> 5) & 7}";
            }
            else
            {
                kind = ctrl switch { 0x3F => "SABM", 0x73 => "UA", 0x53 => "DISC", 0x1F => "DM", 0x87 => "FRMR", _ => $"U 0x{ctrl:X2}" };
            }
            Console.WriteLine($"[rx-raw] a=0x{(body.Length > 0 ? body[0] : 0):X2} {kind} [{Convert.ToHexString(body)}]");
        };

        // Time-based output pump (runs on the adapter loop thread, no locking needed). A multi-chunk
        // terminal reply is streamed as 255-byte continuation PAIRS spaced ~46 ms apart (22.16): the SECOND
        // chunk of each pair has no inbound trigger, so DrainPending must be re-driven on a timer to release
        // it once the gap elapses. serverHost.DrainPending walks each active session's burst and returns the
        // now-permitted frames; we send them straight down the codec, same as the inbound-driven path.
        // A ONE-SHOT TRANSFER NOW ENDS THE RUN WHEN IT IS DONE.
        //
        // It did not, and the runner sat on the seam for the whole --for window - an HOUR by
        // default - after the file had been written. MEASURED 2026-08-18: a --pull wrote its
        // 107603 bytes at 09:05 and the process was still there sending keepalive RRs at 09:21,
        // holding the only seam so the NEXT transfer could not start at all.
        //
        // That cost more than a wait. tools/planc-build.ps1 says in its own NOTES "every runner it
        // starts is a one-shot that exits by itself", which was simply not true, and a script that
        // fetches two listings in a row blocks for ever on the first. The way out was killing the
        // process, and a hard kill leaves D100's LAPB half-open - so the tidy-up for this bug was
        // itself a bug generator.
        //
        // Cancelling a LINKED source rather than returning early keeps the ordinary exit path: the
        // Stopping handler below still runs, TAD sessions are still logged out before the flush,
        // and --for still applies as the outer bound.
        //
        // The daemon is deliberately NOT covered. pushRun and pullRun are set only by --push and
        // --pull; --sync is a service and is supposed to stay up.
        using CancellationTokenSource oneShotDone = CancellationTokenSource.CreateLinkedTokenSource(token);

        // Set once the transfer reports Finished, so the last frames get a tick or two to reach the
        // wire before the loop is asked to stop. Stopping on the very same tick raced the closing
        // ack out of the door.
        DateTime finishedAt = DateTime.MinValue;

        adapter.LoopTick += delegate ()
        {
            // Announce our XMSG restart once the link has settled, so the peer resets the datagram
            // sequence it expects from us. Armed by StatusChanged; sent here because sending from
            // inside that callback re-enters the adapter and drops the link.
            announcer.OnLoopTick();
            nodeHost.Pump();

            // NOT opening the link from the remembered seed. Reverted a SECOND time, and the
            // reason is now precise rather than a suspicion.
            //
            // With the seed, a transfer originates the moment LAPB is up, against a peer that has
            // said nothing. The PUSH survives that because it retries its connect letter four
            // times. The PULL does not - it has no retry - so it fires one letter early, the peer
            // ignores it, and the transfer is dead. MEASURED 2026-08-17: with the seed enabled the
            // pull got a single InitializationNak and nothing else, four runs in a row.
            //
            // So the seed is only safe where a retry backs it. Re-enable it here ONLY after the
            // pull driver has the same bounded retry the push has - otherwise it makes the
            // higher-priority path worse to make the lower-priority one testable.

            // The push rides the same tick. It sends nothing until the link is UP, because a
            // frame we originate before then is simply lost, and nothing after it makes sense. The
            // driver returns nothing while it waits for the server, so the ladder paces itself
            // instead of being paced by this timer.
            //
            // IsUp, NOT State == Connected. Spec 3.1: the link is up when BOTH directions have
            // completed a SABM -> UA exchange, and the state machine reaches CONNECTED on the
            // FIRST of them. MEASURED 2026-08-20: gating on the state sent the connect letter 20 ms
            // before the peer's UA arrived, SendData refused it as not-Active, and the push then
            // failed reporting that node 100 "answered none of 4 connect letters" - which was true,
            // because none had been sent. Same conflation as LapbLayerAdapter had; fixing one and
            // not the other just moved the symptom.
            if (pushRun != null)
            {
                pushRun.Pump(nodeHost, link.IsUp);
            }

            // The pull rides the same tick, gated the same way. Only one of the two can be set -
            // the runner refuses --push and --pull together.
            if (pullRun != null)
            {
                pullRun.Pump(nodeHost, link.IsUp);
            }

            if (batchRun != null)
            {
                batchRun.Pump(nodeHost, link.IsUp);
            }

            chatLoadRun?.Pump(nodeHost, link.IsUp);

            // The daemon scans on its own timer and carries a file on this tick.
            syncDaemon?.Pump(DateTime.UtcNow);

            // ONE-SHOT DONE? Note the moment, then stop a short grace later so the closing frames
            // of the ladder are actually on the wire. Both drivers report Finished either way -
            // a completed transfer and a refused one both end the run, because both are answers.
            bool oneShotFinished =
                (pullRun != null && pullRun.Finished) || (pushRun != null && pushRun.Finished);

            if (oneShotFinished && finishedAt == DateTime.MinValue)
            {
                finishedAt = DateTime.UtcNow;
            }

            if (finishedAt != DateTime.MinValue
                && DateTime.UtcNow - finishedAt > TimeSpan.FromMilliseconds(750)
                && !oneShotDone.IsCancellationRequested)
            {
                Console.WriteLine("[runner] the one-shot transfer is done; closing the link.");
                oneShotDone.Cancel();
            }
        };

        // LOG THE USERS OUT WHILE WE CAN STILL TRANSMIT.
        //
        // Fires on the pump's own thread once it has decided to stop, before the closing flush, so
        // these frames actually reach the wire. Stopping the runner with sessions open killed D100's
        // XMSG on 2026-08-17 - it was left holding half a session each and took a fatal internal
        // inconsistency, costing an emulator restart.
        //
        // This covers the ORDINARY exit: a cancelled token, Ctrl-C, the --for window elapsing. A
        // forced kill runs none of it, so the operating rule stands as well - get the users out
        // before stopping a server the ND is still talking to.
        adapter.Stopping += delegate ()
        {
            IReadOnlyList<XmsgFrame> teardown = tadServer.ShutdownAllSessions(nodeHost.ServerHost);
            if (teardown.Count == 0)
            {
                return;
            }

            Console.WriteLine(
                $"[tad] stopping: logging out the open session(s), {teardown.Count} frame(s).");
            nodeHost.SendFrames(teardown);
        };

        // AND SAY GOODBYE AT THE LINK LAYER TOO.
        //
        // This is the fix for the single most expensive recurring nuisance in this project: after
        // almost every run, the NEXT connection sat repeating SABM for ever and had to be rescued
        // by hand from the machine -
        //
        //     X-C: STOP-LINK / 1362 / <CR>   then   START-LINK,1362,,,-1,,
        //
        // It has been recorded as a consequence of HARD-KILLING the runner, and that is true but
        // not the whole truth: it happened on ORDINARY exits too, because LapbLayer.Disconnect
        // existed and was never called by anything. We closed the socket and left D100 holding a
        // link it still believed was up, so our next SABM was talking to a peer that thought it
        // was already connected.
        //
        // Stopping fires on the pump's own thread before the closing flush, so a DISC queued here
        // actually reaches the wire - exactly the same reason the TAD logout above sits in this
        // handler rather than after the loop.
        adapter.Stopping += delegate ()
        {
            if (link.State == LapbLayerState.Disconnected
                || link.State == LapbLayerState.DiscSent)
            {
                return;
            }

            Console.WriteLine("[link] stopping: sending DISC so the peer does not hold the link.");
            link.Disconnect(Environment.TickCount64);
        };

        adapter.Initiate();
        Console.WriteLine("[runner] SABM sent; pumping seam link (LAPB + codec + XmsgLayer)...");
        // Tick every 20 ms so (a) the LAPB T1/T3/N2 timers run and (b) the output pump above can release a
        // continuation pair's second chunk within the ~46 ms intra-pair window. The idle branch only TICKS
        // + pumps; it does NOT flood the link with RRs (the T3 keepalive poll is emitted from inside Tick),
        // so a healthy idle link stays near-silent and DrainPending returns nothing when no burst is active.
        // (Was 1 s: too coarse to reproduce the measured 45-47 ms intra-pair spacing.)
        await adapter.RunAsync(oneShotDone.Token, keepaliveInterval: TimeSpan.FromMilliseconds(20));
    }

    /// <summary>
    /// Builds this runner's XSGSY routing table relative to the node number we are impersonating.
    /// </summary>
    /// <param name="node">
    /// The system number this runner presents on the wire (the runner's <c>ownNode</c> argument).
    /// </param>
    /// <returns>
    /// The routing-table entries, self-consistent for whichever node we run as.
    /// </returns>
    /// <remarks>
    /// The table MUST be built relative to <paramref name="node"/>, otherwise the XSGSY server hands
    /// node 100 a route that loops back through itself. Concretely, the old fixed table always carried
    /// a "102 reachable Via 100" entry. When the runner is started AS node 102 that entry shadows the
    /// correct "102 is Local (this is me)" self-entry - <see cref="InMemoryRoutingTable.TryLookup"/>
    /// returns the first <c>System >= query</c>, which is the Via-100 entry - so 100's <c>li-rout</c>
    /// reports <c>*->102->100->102...  *Loop suspected*</c> and connect-to routing breaks.
    /// Rules:
    ///  - Our own <paramref name="node"/> is ALWAYS Local (0 hops); never advertise a route to ourselves via anyone.
    ///  - 100 is the direct HDLC link peer (Neighbour), unless we ourselves are 100.
    ///  - 102 is the TAD responder reached through 100 (Via, 2 hops), unless we ourselves are 102.
    /// </remarks>
    /// <summary>
    /// Runs the node over a COSMOS Ethernet segment instead of an HDLC bridge:
    /// EthernetBackend -> EthernetLink(ILink) -> XmsgNodeHost.
    /// </summary>
    /// <param name="topology">
    /// The loaded topology, for the segment settings.
    /// </param>
    /// <param name="peer">
    /// The Ethernet neighbour this node speaks to.
    /// </param>
    /// <param name="node">
    /// The system number this runner presents on the wire.
    /// </param>
    /// <param name="routingEntries">
    /// The routes this node advertises.
    /// </param>
    /// <param name="motdLine">
    /// The TAD banner middle line, or null for the built-in one.
    /// </param>
    /// <param name="users">
    /// The TAD login accounts.
    /// </param>
    /// <param name="pushRun">
    /// The file push, or null when none was asked for. It sends nothing until the peer has
    /// addressed us, because the peer's link id is learned and cannot be derived.
    /// </param>
    /// <param name="token">
    /// Stops the pump.
    /// </param>
    /// <returns>
    /// A task that completes when the pump stops.
    /// </returns>
    /// <remarks>
    /// The composition above the link is IDENTICAL to the HDLC path - the same
    /// <see cref="XmsgNodeHost"/> - because the SINTRAN header is transport-independent. What is
    /// missing here compared to the HDLC path is the LAPB diagnostics, which have no Ethernet
    /// equivalent: the ND link layer has no SABM, no window and no retransmission.
    /// </remarks>
    private static async Task RunEthernetSeamAsync(
        TopologyConfig topology, TopologyNode peer, ushort node,
        IReadOnlyList<RoutingTableEntry> routingEntries, string? motdLine,
        TadUserDirectory users, FaServer? fileServer, FaPushRun? pushRun, FaPullRun? pullRun,
        bool traceFrames,
        bool announceRestart,
        bool resyncHard,
        string? syncFolder,
        string? syncPullFolder,
        string? syncUser,
        string? syncToNode,
        string? batchInput,
        string? batchOutput,
        bool requestLink,
        CancellationToken token)
    {
        // The segment settings come from OUR entry when we have one, otherwise from the peer's -
        // both machines must be pointed at the same group for anything to arrive.
        TopologyNode? self = topology.FindById(topology.Self);
        string spec = TopologyConfig.EthernetSpec(self?.Ethernet ?? peer.Ethernet);

        IEthernetBackend? backend = EthernetBackendFactory.FromSpec(spec);
        if (backend == null)
        {
            Console.WriteLine($"[runner] ethernet backend spec not supported: {spec}");
            return;
        }

        string linkId = $"eth:{peer.Alias ?? peer.Id.ToString()}";
        EthernetLink link = new EthernetLink(linkId, node, peer.Id, backend);

        // SEGMENT SNIFFER. The link itself only accepts frames from its own peer, which is correct
        // but leaves us blind to everything else on a shared segment - and "nothing happened" is
        // exactly the report that needs evidence behind it. This logs EVERY frame that reaches us,
        // whoever sent it, so we can see who talks, to whom, and when. It observes only; the link's
        // own handler does the work.
        backend.OnPacketReceived += delegate (byte[] data, int length)
        {
            // OFF unless asked for: this runs on the hot receive path and its hex dumps once cost
            // us a 1.77 s median acknowledgement against a 0.19 s peer retransmit timer.
            if (!traceFrames)
            {
                return;
            }

            if (!Ieee8023Frame.TryParse(
                    new ReadOnlySpan<byte>(data, 0, length),
                    out NdMacAddress destination, out NdMacAddress source,
                    out int payloadOffset, out int payloadLength))
            {
                Console.WriteLine($"[sniff] {length}B frame that is not ND/COSMOS 802.3+LLC1");
                return;
            }

            // Frames we sent ourselves come back on a multicast segment; skip our own.
            if (source.Equals(link.LocalMac))
            {
                return;
            }

            string from = DescribeStation(source);
            string to = DescribeStation(destination);

            if (payloadLength >= NdLinkHeader.Length
                && NdLinkHeader.TryParse(
                    new ReadOnlySpan<byte>(data, payloadOffset, payloadLength), out NdLinkHeader ndHeader))
            {
                string kind = ndHeader.IsData ? "data" : ndHeader.IsAcknowledge ? "ack" : $"kind0x{ndHeader.Kind:X2}";
                Console.WriteLine(
                    $"[sniff] {from} -> {to}  {kind} seq={ndHeader.Sequence} " +
                    $"snd={ndHeader.SenderLinkId} rcv={ndHeader.ReceiverLinkId} plen={ndHeader.PayloadLength}" +
                    (destination.Equals(link.LocalMac) ? "   <== ADDRESSED TO US" : string.Empty));

                // Full hex of anything that is NOT the two kinds we already understand, and of
                // anything aimed at us. A frame kind nobody has captured cannot be decoded from a
                // field summary - the bytes are the evidence, and guessing at the layout instead is
                // exactly what this project has been burned by.
                if (!ndHeader.IsData && !ndHeader.IsAcknowledge || destination.Equals(link.LocalMac))
                {
                    Console.WriteLine($"[sniff]   llc+nd : {Convert.ToHexString(data, payloadOffset, payloadLength)}");
                    Console.WriteLine($"[sniff]   frame  : {Convert.ToHexString(data, 0, length)}");
                }
            }
            else
            {
                Console.WriteLine($"[sniff] {from} -> {to}  {payloadLength}B (no ND link header)");
            }
        };

        Console.WriteLine(
            $"[runner] joining COSMOS segment {spec} as node {node} " +
            $"(MAC {Describe(link.LocalMac)}), peer {peer.Id} (MAC {Describe(link.PeerMac)})");

        string statePath = System.IO.Path.Combine(AppContext.BaseDirectory, "xmsg-sequence.state");
        FileResponderSequenceStore sequenceStore = new FileResponderSequenceStore(statePath);
        Console.WriteLine($"[runner] datagram-sequence state file: {statePath}");

        RetroIO.Protocols.Xmsg.Servers.Tad.TadServer tadServer =
            new RetroIO.Protocols.Xmsg.Servers.Tad.TadServer(() => DateTime.Now, users: users, motdLine: motdLine);
        Console.WriteLine($"[tad] {users.Count} login account(s) loaded");
        tadServer.SessionOpened += (tadNumber, clientSystem) =>
            Console.WriteLine($"[tad] session opened: tty{tadNumber} from node {clientSystem}");
        tadServer.SessionClosed += (tadNumber, clientSystem) =>
            Console.WriteLine($"[tad] session closed: tty{tadNumber} from node {clientSystem}");

        XmsgNodeHost nodeHost = new XmsgNodeHost(
            link, node, routingEntries, sequenceStore, BuildServerList(tadServer, fileServer));

        // THE SEQUENCE INSTRUMENTATION HAS TO REACH THE LOG. XmsgServerHost logs the value a link
        // STARTS from, every Flags 1 it stamps, and every store advance - all added deliberately to
        // answer "is our number behind the peer's expectation, or did the peer drop the frame for
        // another reason", which the wire alone cannot tell apart because the peer's expectation of
        // US is a value it never transmits.
        //
        // The relay path wired this; the Ethernet path never did, so on 2026-08-11 an afternoon of
        // sequence work was done on inference from acknowledgements instead of on the numbers we
        // actually stamped. A diagnostic that is not connected is not a diagnostic.
        nodeHost.ServerHost.Log = line => Console.WriteLine(line);

        // REMEMBER THE ENVELOPE SEED. It is a per-link constant that survives the peer rebooting,
        // and remembering it is what lets this process address a machine it has met before without
        // waiting to be spoken to first. Nothing is invented: a node we have never heard from stays
        // unreachable and says so.
        string seedPath = System.IO.Path.Combine(AppContext.BaseDirectory, "xmsg-link-seed.state");
        nodeHost.ServerHost.SeedStore = new FileLinkSeedStore(seedPath);
        Console.WriteLine($"[runner] link-seed state file: {seedPath}");

        // The node's OWN log, which is a different sink from the server host's. It carries the
        // reason a peer rejected a frame - the subtype-0x07 network error and its XE code. Not
        // connected until 2026-08-11, so a push that D100 was actively refusing read as silence.
        nodeHost.Layer.Log = line => Console.WriteLine(line);

        tadServer.ServerDirectory = nodeHost.ServerHost.DescribeServers;
        tadServer.RouteReport = nodeHost.FormatRouteReport;

        // Declared before the frame handler that captures it, and filled in further down once
        // the peer and link it needs are known.
        SyncDaemon? syncDaemon = null;
        AppendRemoteBatchRun? batchRun = null;
        ChatLoadRun? chatLoadRun = null;

        nodeHost.Layer.MessageReceived += delegate (string id, XmsgPacketInfo packet)
        {
            XmsgFrame f = packet.Frame;

            // The full hex of every inbound message is trace-only: it runs before the reply is
            // built, so it lands directly on the acknowledgement latency the peer is timing.
            if (traceFrames)
            {
                Console.WriteLine(
                    $"[RX] {f.Header.SourceNode}->{f.Header.DestinationNode} " +
                    $"sub={f.Header.Subtype} f1=0x{f.Header.Flags1:X4} " +
                    $"info={Convert.ToHexString(packet.RawBytes)}");
            }

            // A push is a conversation we started, so its replies arrive here like anything else.
            // The driver decides which frames are its own.
            // The daemon's transfer sees every inbound frame; its drivers pick out their own.
            syncDaemon?.OnFrame(f);
            batchRun?.OnFrame(f);
            chatLoadRun?.OnFrame(f);

            if (pushRun != null)
            {
                pushRun.OnFrame(f);
            }

            // A pull is the same kind of conversation, and its content arrives the same way. The
            // frame IS reassembled by the time it gets here - the layer rejoins a fragment pair
            // before raising this event. It did NOT until 2026-08-11, and that is exactly why a
            // pull could run the whole read ladder and still write no file: the driver was handed
            // two halves it had no way to use. See XmsgNode.AcceptFragment.
            if (pullRun != null)
            {
                pullRun.OnFrame(f);
            }
        };

        // The first half of a split message. Nothing can be answered until its continuation lands,
        // but a frame that vanishes with no line in the log is expensive to diagnose - this one
        // used to appear as "NO REPLY BUILT", which reads like a fault and is not one.
        nodeHost.Layer.FragmentHeld += delegate (string id, XmsgPacketInfo packet)
        {
            XmsgFrame f = packet.Frame;
            Console.WriteLine(
                $"[frag] {f.Header.SourceNode}->{f.Header.DestinationNode} first fragment held at " +
                $"Flags1 0x{f.Header.Flags1:X4}, {f.GetBodyBytes().Length} byte(s); waiting for its continuation");
        };

        // ANSWERED-OR-NOT. An inbound datagram that produces no reply hangs the calling SINTRAN
        // terminal (ESC will not abort it), and until now that looked identical in the log to a
        // datagram that WAS answered. Log the answer count against every request so
        // "received but unanswered" can never be silent again.
        nodeHost.Layer.DispatchCompleted += delegate (string id, XmsgPacketInfo packet, int produced)
        {
            XmsgFrame f = packet.Frame;
            if (produced == 0)
            {
                // NOT EVERY ARRIVAL WANTS AN ANSWER. An acknowledgement, a reachability reply and a
                // network error are all the END of an exchange - answering one would be the bug. The
                // alarm used to fire for those too, so a healthy run carried three lines reading
                // "*** NO REPLY BUILT *** (this hangs the caller)" and nothing was hanging at all.
                //
                // That is not merely noise. On 2026-08-17 those lines were read as evidence while
                // chasing a real hang, and the investigation went one layer too low because of it. A
                // diagnostic that cries wolf on a healthy exchange is worse than none.
                if (!ExpectsAReply(f.Header.Subtype))
                {
                    Console.WriteLine(
                        $"[RX] {f.Header.SourceNode}->{f.Header.DestinationNode} " +
                        $"sub={f.Header.Subtype} (no reply expected)");
                    return;
                }

                Console.WriteLine(
                    $"[RX] {f.Header.SourceNode}->{f.Header.DestinationNode} " +
                    $"sub={f.Header.Subtype} *** NO REPLY BUILT *** (this hangs the caller)");
                return;
            }

            Console.WriteLine(
                $"[RX] {f.Header.SourceNode}->{f.Header.DestinationNode} " +
                $"sub={f.Header.Subtype} answered with {produced} frame(s)");
        };

        // A reply the stack built but the link refused never reaches the wire. That is a DIFFERENT
        // fault from building no reply at all, and it lives in different code, so it gets its own
        // line rather than being folded into the one above.
        nodeHost.Transport.SendRefused += delegate (ILink refusing, int byteCount)
        {
            Console.WriteLine(
                $"[TX] *** DROPPED *** {refusing.Name} refused a {byteCount}B reply " +
                $"(status {refusing.Status}); total refused {nodeHost.Transport.RefusedFrames}");
        };

        // A reply the link TOOK but has not sent. The third outcome, and the one that had no line
        // at all until 2026-08-17: a TAD connect from D100 hung with "answered with 2 frame(s)" in
        // the log and nothing on the wire, because a queued datagram reported the same success as a
        // transmitted one. Depth is included because a single park is ordinary and a rising depth
        // is the peer having stopped acknowledging us.
        link.DatagramParked += delegate (EthernetLink parking, int byteCount, int queueDepth)
        {
            Console.WriteLine(
                $"[TX] *** PARKED *** {parking.Name} took a {byteCount}B frame but the send window "
                + $"is full; {queueDepth} datagram(s) now waiting and NOTHING is leaving this node "
                + $"until the peer acknowledges us. {parking.ParkedDetail}");
        };

        // A STALE ACKNOWLEDGEMENT. Alone it is a duplicate in flight and means nothing; a run of
        // them WITH a climbing park queue is the deadlock - the frames the peer needs in order to
        // move forward are the ones stuck behind our window. The two lines together are the
        // signature, which is why they are logged in the same shape.
        link.StaleAcknowledgement += delegate (EthernetLink stale, byte acknowledged, byte expected, int queued)
        {
            Console.WriteLine(
                $"[RX] *** STALE ACK *** {stale.Name} acknowledged 0x{acknowledged:X2} but we already "
                + $"had the peer at 0x{expected:X2}; refused, and {queued} datagram(s) are parked. "
                + "A RUN of these with a climbing queue is a deadlock, not a duplicate.");
        };

        link.StatusChanged += delegate (ILink changed, LinkStatus older, LinkStatus newer, string reason)
        {
            Console.WriteLine($"[link] {changed.Name} status {older} -> {newer} ({reason})");
        };

        // ANNOUNCE OURSELVES so the peer resets the datagram sequence it expects from us.
        //
        // Off unless asked for. MEASURED 2026-08-11 by diffing the one fully working Ethernet run
        // against every later one:
        //
        //   the run that WORKED : D100 opened the link itself - a connection confirm (kind 0x1F)
        //                         and a reachability exchange (Flags 1 0xFFFF). Both sides then
        //                         started from datagram Flags 1 0x0000 and the whole listing ran.
        //   every run after it  : neither happened. D100 reused its old link reference across our
        //                         restarts and kept the datagram sequence it expected FROM us,
        //                         while we opened at 0x0000. It dropped us in SILENCE - no error,
        //                         no reject - and the listing and a file pull both died.
        //
        // A reachability request is the documented resynchronisation and it is exactly what D100
        // sent in the run that worked, so sending one ourselves is the obvious thing to try. It is
        // an option rather than a default because the ONE time an announce was measured it did
        // harm - on HDLC, where it dropped LAPB 260 ms later (see AnnounceRestartOnLinkUp in the
        // HDLC path). Ethernet has no LAPB to drop, so that harm cannot repeat here, but "cannot
        // repeat for that reason" is not the same as "measured safe".
        //
        // Arm on the status change, send on the next loop tick - never from inside the callback,
        // which on HDLC re-entered the adapter mid-transition and took the link down.
        // THE FOLDER-WATCH DAEMON. Built here because this is where the node, the link and the
        // peer all exist. It scans on its own timer and carries on the loop tick below.
        if (syncFolder != null)
        {
            ushort syncNode;
            if (syncToNode == null || !ushort.TryParse(syncToNode, out syncNode))
            {
                syncNode = peer.Id;
            }

            RetroXmsg.Runner.TopologyNode? syncPeer = topology?.FindById(syncNode);
            string syncMachine = (syncPeer != null && !string.IsNullOrEmpty(syncPeer.Alias))
                ? syncPeer.Alias!.ToUpperInvariant()
                : "D" + syncNode;

            syncDaemon = new SyncDaemon(
                syncFolder,
                syncMachine,
                syncUser ?? "SYSTEM",
                nodeHost,
                syncNode,
                () => link.HasLearnedPeer,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(5),
                syncPullFolder);
        }

        if (batchInput != null)
        {
            RetroXmsg.Runner.TopologyNode? batchPeer = topology?.FindById(peer.Id);
            string batchMachine = (batchPeer != null && !string.IsNullOrEmpty(batchPeer.Alias))
                ? batchPeer.Alias!.ToUpperInvariant()
                : "D" + peer.Id;

            batchRun = new AppendRemoteBatchRun(
                peer.Id, batchMachine, "SYSTEM", batchInput, batchOutput ?? "ARBOUT:SYMB");
        }


        LinkAnnouncer? ethernetAnnouncer = null;
        if (announceRestart)
        {
            ethernetAnnouncer = new LinkAnnouncer(
                () => link.Status,
                () =>
                {
                    for (int i = 0; i < routingEntries.Count; i++)
                    {
                        RoutingTableEntry entry = routingEntries[i];

                        // Direct neighbours only. Ourselves, and anything reached THROUGH another
                        // node, are not ours to announce to.
                        if (entry.System == node || entry.Hops != 1)
                        {
                            continue;
                        }

                        Console.WriteLine(
                            $"[announce] telling system {entry.System} that our XMSG restarted, so it "
                            + "resets the datagram sequence it expects from us");

                        if (resyncHard)
                        {
                            // Zeroing our own counter here is what D103 proved wrong over HDLC.
                            //
                            // MEASURED 2026-08-17 on D100 over the seam: it does not reset either.
                            // Its X-C LIST-SYSTEMS "Sequence no. Send-receive" columns held at 64/65
                            // through an announce and a --request-link, and the 0x0000 frames that
                            // followed were dropped without any error. See the matching note on the
                            // seam path. This flag manufactures a drift rather than curing one.
                            // Name the value being discarded - see the matching note on the seam path.
                            ushort storedFlags1 = sequenceStore.LoadNextFlags1(entry.System);
                            Console.WriteLine(
                                $"[announce] --resync-hard: also zeroing OUR counter for {entry.System}."
                                + $" Discarding the stored next-Flags1 0x{storedFlags1:X4}."
                                + " The next originated frame goes out at 0x0000 - if the peer accepts"
                                + " it, the peer resets on our announce; if it is dropped in silence,"
                                + " it does not.");
                            if (storedFlags1 != 0)
                            {
                                Console.WriteLine(
                                    $"[announce] WARNING: node {entry.System} was at 0x{storedFlags1:X4}, not a"
                                    + " fresh contact. If the peer does NOT reset on our announce, every"
                                    + " frame from here is behind-sequence and dropped without an error.");
                            }

                            nodeHost.AnnounceRestartAndResetOurs(entry.System);
                        }
                        else
                        {
                            nodeHost.AnnounceRestart(entry.System);
                        }
                    }
                },
                enabled: true,
                onceOnly: true);

            link.StatusChanged += delegate (ILink changed, LinkStatus older, LinkStatus newer, string reason)
            {
                ethernetAnnouncer.OnStatusChanged(newer);
            };
        }

        // Everything WE put on the wire, decoded the same way the sniffer decodes inbound frames, so
        // the two sides of a conversation can be read against each other in one log.
        link.OnFrameTransmitted += delegate (byte[] frame, int length)
        {
            // OFF unless asked for - same hot-path cost as the sniffer above.
            if (!traceFrames)
            {
                return;
            }

            if (!Ieee8023Frame.TryParse(
                    new ReadOnlySpan<byte>(frame, 0, length),
                    out NdMacAddress destination, out NdMacAddress source,
                    out int payloadOffset, out int payloadLength))
            {
                Console.WriteLine($"[tx] {length}B frame that is not ND/COSMOS 802.3+LLC1");
                return;
            }

            if (payloadLength >= NdLinkHeader.Length
                && NdLinkHeader.TryParse(
                    new ReadOnlySpan<byte>(frame, payloadOffset, payloadLength), out NdLinkHeader txHeader))
            {
                string kind = txHeader.IsData ? "data" : txHeader.IsAcknowledge ? "ack" : $"kind0x{txHeader.Kind:X2}";
                Console.WriteLine(
                    $"[tx] {DescribeStation(source)} -> {DescribeStation(destination)}  {kind} " +
                    $"seq={txHeader.Sequence} snd={txHeader.SenderLinkId} rcv={txHeader.ReceiverLinkId} " +
                    $"plen={txHeader.PayloadLength}");
                Console.WriteLine($"[tx]   llc+nd : {Convert.ToHexString(frame, payloadOffset, payloadLength)}");
            }
            else
            {
                Console.WriteLine($"[tx]   frame  : {Convert.ToHexString(frame, 0, length)}");
            }
        };

        link.OnUnknownFrameKindReceived += kind =>
            Console.WriteLine(
                $"[link] unknown ND frame kind 0x{kind:X2} (captured so far: 0x0F CR, 0x20 DT, 0x3F AK, 0x60/0x6F DR)");

        // A repeat is NEVER normal on this link, and it is the single cheapest thing to look at
        // before suspecting any layer above. On 2026-08-10 D100 repeated frames for seconds on end
        // because we were sending far ahead of the link's window; every repeat arrived at the file
        // server as a fresh request and it answered each with a new session counter and connection
        // number, ending in SINTRAN error 267 octal. It took two nights to attribute, because the
        // only evidence was byte-identical frames in a log nobody was counting. Now it says so.
        link.OnDuplicateDataFrameReceived += (sequence, expected) =>
            Console.WriteLine(
                $"[link] WARNING the peer RE-SENT data frame seq={sequence} (we were waiting for {expected}). " +
                "It has not seen our acknowledgement - check the send window and our own receive latency " +
                "BEFORE looking for a fault in XMSG or the file server.");

        // High nibble 6 is the disconnect-request family. D100 sends 0x60 repeatedly once it gives
        // up on a conversation; before 2026-08-04 that fell through to UnknownFrameKindReceived and
        // filled the log. What the LOW nibble means is UNVERIFIED - see NdLinkFrameKind.
        link.OnDisconnectRequested += kind =>
            Console.WriteLine($"[link] disconnect request from the peer, ND frame kind 0x{kind:X2}");

        link.Start();

        // Ask the peer to open the link again BEFORE anything else goes out. On Ethernet a peer
        // keeps its idea of our datagram sequence across our restarts, and re-opening the link
        // is the only thing ever observed to clear it - see NdLinkLayer.SendConnectionRequest.
        if (requestLink)
        {
            Console.WriteLine(
                "[link] asking node " + peer.Id + " to open the ND link again, so it forgets the "
                + "sequence it expects from our previous run");
            link.RequestConnection();
        }

        // The link CANNOT open the conversation: a data frame carries the peer's link id in its
        // receiver field, and that id is learned from the peer, never derived. So we sit here until
        // the ND machine addresses us. If it never does, that is a real finding, not a hang - say so
        // rather than looking dead.
        Console.WriteLine(
            $"[runner] waiting for node {peer.Id} to send us a frame (we cannot address it until it does)...");

        bool announced = false;
        DateTime started = DateTime.UtcNow;

        // Heartbeat while we wait. Silence is ambiguous - it could mean "the segment is dead" or
        // "the segment is busy but nobody is addressing us", and those need completely different
        // fixes. Reporting what we HEAR tells the two apart without a packet capture (which would
        // not work here anyway: host-local multicast never reaches npcap).
        DateTime lastHeartbeat = DateTime.UtcNow;
        long lastOtherStations = 0;

        // 20 ms so the TAD continuation-pair pump can release a second chunk inside the measured
        // ~46 ms intra-pair window (TAD-Message-Formats.md 22.16).
        while (!token.IsCancellationRequested)
        {
            nodeHost.Pump();

            // Send the armed announce here rather than from the status callback - see the note
            // where the announcer is built.
            if (ethernetAnnouncer != null)
            {
                ethernetAnnouncer.OnLoopTick();
            }

            if (!announced && link.HasLearnedPeer)
            {
                announced = true;
                Console.WriteLine($"[runner] peer {peer.Id} heard after {(DateTime.UtcNow - started).TotalSeconds:F1}s; link is up");
            }

            // HasLearnedPeer is exactly the right gate: it is true once the peer's link id has
            // arrived in a frame from the peer, which is the one thing a frame WE originate cannot
            // do without. Before that, anything we sent would carry a receiver id we invented.
            if (pushRun != null)
            {
                pushRun.Pump(nodeHost, link.HasLearnedPeer);
            }

            // The daemon rides the same tick, for the same reason: this loop is what also answers
            // the machine at the other end, so nothing here may block.
            syncDaemon?.Pump(DateTime.UtcNow);
            batchRun?.Pump(nodeHost, link.HasLearnedPeer);
            chatLoadRun?.Pump(nodeHost, link.HasLearnedPeer);

            // The pull rides the same tick, gated on the same learned peer id.
            if (pullRun != null)
            {
                pullRun.Pump(nodeHost, link.HasLearnedPeer);
            }

            if (DateTime.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(5))
            {
                lastHeartbeat = DateTime.UtcNow;
                long other = link.FramesFromOtherStations;
                long delta = other - lastOtherStations;
                lastOtherStations = other;

                if (!announced)
                {
                    Console.WriteLine(
                        $"[segment] still waiting for {peer.Id}. Frames from other stations: {other} " +
                        $"(+{delta} in the last 5s). " +
                        (other == 0
                            ? "NOTHING at all is arriving - either no ND machine is on this group, or they have not been restarted since the ini change."
                            : "The segment IS carrying traffic, so the group is right - it is just not coming from our peer."));
                }
                else
                {
                    Console.WriteLine(
                        $"[segment] data={link.DataFramesReceived} acks={link.AcknowledgementsReceived} " +
                        $"other-stations={other}");
                }
            }

            try
            {
                await Task.Delay(20, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (!announced)
        {
            Console.WriteLine(
                $"[runner] node {peer.Id} never sent us a frame. The link stayed in {link.Status}, so nothing " +
                $"could be sent. Frames seen from other stations: {link.FramesFromOtherStations}.");
        }

        link.Dispose();
    }

    /// <summary>
    /// Formats a station address as the usual colon-separated hex, for logs.
    /// </summary>
    /// <param name="mac">
    /// The address to format.
    /// </param>
    /// <returns>
    /// The formatted address.
    /// </returns>
    /// <summary>
    /// Names a station by its ND system number when the address carries the ND vendor prefix,
    /// falling back to the raw address.
    /// </summary>
    /// <param name="mac">
    /// The station address.
    /// </param>
    /// <returns>
    /// Something readable for a log line.
    /// </returns>
    private static string DescribeStation(NdMacAddress mac)
    {
        // The system number is stored byte-REVERSED in the MAC; TryGetSystemNumber undoes that.
        if (mac.TryGetSystemNumber(out ushort system))
        {
            return $"node{system}";
        }

        return Describe(mac);
    }

    private static string Describe(NdMacAddress mac)
    {
        Span<byte> bytes = stackalloc byte[NdMacAddress.Length];
        mac.Write(bytes);

        System.Text.StringBuilder sb = new System.Text.StringBuilder(17);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(':');
            }

            sb.Append(bytes[i].ToString("X2"));
        }

        return sb.ToString();
    }

    // The route report now lives on XmsgNodeHost, so every transport formats it identically.

    private static List<RoutingTableEntry> BuildRoutingEntries(ushort node)
    {
        List<RoutingTableEntry> entries = new List<RoutingTableEntry>(3);

        // Self is always directly local - this is the entry that keeps 100 from routing to us via 100.
        entries.Add(new RoutingTableEntry(node, XroutConnectionType.Local, node, 0, 0));

        // 100 is the machine on the other end of the HDLC bridge: a direct neighbour (1 hop).
        if (node != 100)
        {
            entries.Add(new RoutingTableEntry(100, XroutConnectionType.Neighbour, 1, 1, 0));
        }

        // 102 (the TAD terminal server) is reached through 100 - but only when WE are not 102 ourselves,
        // otherwise we would advertise a Via-100 loop back to our own node number.
        if (node != 102)
        {
            entries.Add(new RoutingTableEntry(102, XroutConnectionType.Via, 100, 2, 0));
        }

        return entries;
    }

    /// <summary>
    /// The original LiveNode + XmsgNode composition, retained until the seam path is validated live.
    /// </summary>
    private static async Task RunLegacyAsync(
        TcpBridgeTransport transport, ushort node,
        IReadOnlyList<RoutingTableEntry> routingEntries, CancellationToken token)
    {
        LapbLayer link = new LapbLayer(node);
        XmsgNode xnode = new XmsgNode(node, 0x00);
        xnode.AcknowledgeData = false;

        xnode.RoutingTable = new InMemoryRoutingTable(routingEntries);
        xnode.TadResponder = new RetroIO.Protocols.Xmsg.Node.Tad.TadTerminalResponder(node, () => DateTime.Now);
        xnode.AcknowledgeTadFrames = true;

        link.OnTransmit += body =>
        {
            byte ctrl = body.Length > 1 ? body[1] : (byte)0;
            string kind;
            if ((ctrl & 1) == 0)
            {
                kind = $"I ns={(ctrl >> 1) & 7} nr={(ctrl >> 5) & 7}";
            }
            else if ((ctrl & 3) == 1)
            {
                // S-frame: the supervisory subtype is bits 2-3 (0=RR, 1=RNR, 2=REJ). Do NOT label every
                // S-frame "RR" - that hid 100's REJ (ctrl 0xC9) as an "RR" in the raw log.
                string s = ((ctrl >> 2) & 3) switch { 0 => "RR", 1 => "RNR", 2 => "REJ", _ => "S?" };
                kind = $"{s} nr={(ctrl >> 5) & 7}";
            }
            else
            {
                kind = ctrl switch { 0x3F => "SABM", 0x73 => "UA", 0x53 => "DISC", 0x1F => "DM", 0x87 => "FRMR", _ => $"U 0x{ctrl:X2}" };
            }
            string extra = (ctrl & 1) == 0 ? $" body={Convert.ToHexString(body)}" : string.Empty;
            // V(S)/V(A) ON EVERY FRAME. The FRMR diagnostic said V(S)=0 immediately after we had
            // transmitted I ns=0, which cannot both be true - so the variables themselves have to be
            // on the record, not inferred from what was sent.
            Console.WriteLine($"[TX] a=0x{(body.Length > 0 ? body[0] : 0):X2} {kind} state={link.State}"
                + $" vs={link.SendVariable} va={link.AcknowledgeVariable} vr={link.ReceiveVariable}{extra}");
        };

        LiveNode live = new LiveNode(transport, link, xnode);
        link.Connect(0);
        Console.WriteLine("[runner] SABM sent; pumping legacy link...");
        // No periodic keepalive (matches the original validated runner) - reactive RR only.
        // No timers ON PURPOSE, and now said out loud: the original validated legacy runner emitted
        // no periodic keepalive and answered RR only when spoken to. Reproducing that exactly is
        // the point of this path, so it is the one caller that genuinely wants the no-timer pump.
        await live.RunWithoutTimersAsync(token);
    }
}
