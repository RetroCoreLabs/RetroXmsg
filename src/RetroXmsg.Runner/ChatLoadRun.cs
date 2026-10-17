//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroXmsg - Accessing yesterday's Xmsg API with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Protocols.Xmsg;              // XmsgFrame, XmsgFrameFlags, XmsgSendOptions
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Chat;
using RetroIO.Protocols.Xmsg.Live;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Services;  // XmsgAnsweredFlags1
namespace RetroXmsg.Runner
{
    /// <summary>
    /// Puts many simulated users into a REAL chat room on a real machine, and counts what happens.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists as well as the unit tests</b></para>
    /// <c>RetroIO.Protocols.Xmsg.Chat.Tests/ChatLoadTests.cs</c> already runs twenty users and ten thousand messages,
    /// but in one process with no wire - it proves OUR room, not the machine's. The question this
    /// run answers is the other one: does the PLANC <c>CHATSV</c> on an ND survive twenty users
    /// arriving at once, talking, and leaving.
    /// <para><b>How a join reaches a named room</b></para>
    /// Exactly as <see cref="AppendRemoteBatchRun"/> reaches <c>*XFTRA</c>: an XSLET letter to
    /// XROUT's well-known port 0, with the room name as parameter 1. The chat bytes ride AFTER the
    /// parameter blocks, because the length word at bytes 2-3 counts only the blocks - which is
    /// precisely what CHATSV computes its <c>payloadAt</c> from:
    /// <code>
    /// payloadAt = 4 + inBuf(2) * 256 + inBuf(3)
    /// </code>
    /// So appending is not a trick; it is the layout both sides already agree on.
    /// <para><b>One port per user, and why that matters</b></para>
    /// Each simulated user gets its OWN session port, because a port is what the room identifies a
    /// member by - the server keys its seats on the sender's magic number. Sharing one port would
    /// make twenty users look like one and would test nothing.
    /// <para><b>LEAVING IS PART OF THE TEST, not tidying up</b></para>
    /// A member that vanishes without saying so keeps its seat until something else tries to send
    /// to it - measured on 2026-08-18, and only fixed at all because the server now sends secure
    /// and reaps on the bounce. A load run that just stops would therefore leave the room full and
    /// the NEXT run would measure nothing. So every joined user leaves, and the run says how many
    /// did.
    /// </remarks>
    public sealed class ChatLoadRun
    {
        private readonly ushort _serverNode;
        private readonly string _serverName;

        // THE SERVICE NAME, NOT A ROOM NAME - and that is the change the one-port model made here.
        // This used to be the room: every room registered its own XROUT name, CHAT-LOBBY and
        // CHAT-GENERAL, and a join was addressed to it. Now there is ONE name, *CHAT, and the room
        // travels in the Join's text field. A run left on the old model addresses a name that no
        // longer exists and every join comes back XRUNN.
        private readonly string _serviceName;

        // Which rooms to spread the users over. One entry means the old single-room run; more than
        // one is what makes this able to test isolation AT SCALE, which two terminals cannot.
        private readonly string[] _rooms;

        private readonly int _userCount;
        private readonly int _linesEach;

        private readonly List<ushort> _ports = new List<ushort>();
        private readonly Dictionary<ushort, int> _portToUser = new Dictionary<ushort, int>();

        /// <summary>
        /// The server port each user learned from its welcome, or 0.
        /// </summary>
        private readonly Dictionary<int, ushort> _serverPortFor = new Dictionary<int, ushort>();

        private int _welcomed;
        private int _rejected;
        private int _saidHeard;
        private int _leftSent;
        private int _framesFromRoom;
        private int _undecodable;
        private int _letterRefused;

        /// <summary>
        /// How many pump ticks to keep listening after the talking, and again after the leaving.
        /// </summary>
        /// <remarks>
        /// A Said is a BROADCAST the room makes after it receives a line, so it always arrives
        /// later than the line that caused it. Twenty ticks is arbitrary but generous - the whole
        /// point is that it is not zero, which is what it effectively was.
        /// </remarks>
        private const int SettlePumps = 20;

        /// <summary>
        /// The serial we stamp on every letter, echoed back unchanged by XROUT.
        /// </summary>
        private const byte LetterSerial = 0x7B;

        /// <summary>
        /// The XSLET service number, replaced by a status on a reply.
        /// </summary>
        private const byte XsletService = 65;

        private Phase _phase = Phase.Idle;
        private int _round;

        /// <summary>
        /// What the run is doing at the moment.
        /// </summary>
        private enum Phase
        {
            /// <summary>
            /// Nothing sent yet.
            /// </summary>
            Idle,

            /// <summary>
            /// Join letters are out; waiting for welcomes.
            /// </summary>
            Joining,

            /// <summary>
            /// Members are talking.
            /// </summary>
            Talking,

            /// <summary>
            /// Talking is done; waiting for the room to broadcast it back.
            /// </summary>
            Settling,

            /// <summary>
            /// Members are leaving.
            /// </summary>
            Leaving,

            /// <summary>
            /// Everyone has left; waiting before the summary is printed.
            /// </summary>
            Finishing,

            /// <summary>
            /// Everything is done and the summary is printed.
            /// </summary>
            Done,
        }

        /// <summary>
        /// Creates a load run.
        /// </summary>
        /// <param name="serverNode">
        /// The node the room lives on, for example 100.
        /// </param>
        /// <param name="serverName">
        /// That machine's system name, for example <c>D100</c>. XROUT looks the room up by system.
        /// </param>
        /// <param name="serviceName">
        /// The registered SERVICE name to join, which is <c>*CHAT</c> - not a room. See the field
        /// remarks: rooms stopped being XROUT names when the server moved to one port.
        /// </param>
        /// <param name="rooms">
        /// The rooms to spread the users over, round robin. One entry is a single-room run; two or
        /// more is an isolation test at a scale terminals cannot reach.
        /// </param>
        /// <param name="userCount">
        /// How many simulated users to create.
        /// </param>
        /// <param name="linesEach">
        /// How many lines each welcomed user says.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serverName"/>, <paramref name="serviceName"/> or
        /// <paramref name="rooms"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="rooms"/> is empty.
        /// </exception>
        public ChatLoadRun(
            ushort serverNode,
            string serverName,
            string serviceName,
            string[] rooms,
            int userCount,
            int linesEach)
        {
            _serverNode = serverNode;
            _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
            _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));

            if (rooms == null)
            {
                throw new ArgumentNullException(nameof(rooms));
            }

            if (rooms.Length == 0)
            {
                throw new ArgumentException("A run needs at least one room.", nameof(rooms));
            }

            _rooms = rooms;
            _userCount = userCount;
            _linesEach = linesEach;
        }

        /// <summary>
        /// The room a given user joins.
        /// </summary>
        /// <param name="user">
        /// The user's index.
        /// </param>
        /// <returns>
        /// The room name to put in that user's Join.
        /// </returns>
        /// <remarks>
        /// Round robin rather than blocks, so a run with two rooms interleaves them - the joins
        /// arrive alternating, which is the harder case for a server keeping one flat seat table
        /// with a room name beside each seat.
        /// </remarks>
        private string RoomOf(int user)
        {
            return _rooms[user % _rooms.Length];
        }

        /// <summary>
        /// How many Said messages a correct server should send, given who ended up where.
        /// </summary>
        /// <returns>
        /// The expected count.
        /// </returns>
        /// <remarks>
        /// <para><b>This number IS the isolation test</b></para>
        /// Every line said in a room is broadcast to everybody in THAT room, the speaker included.
        /// So a room of <c>m</c> members saying <c>n</c> lines each produces <c>m * n * m</c> Said
        /// messages, and the total is the sum of <c>m^2 * n</c> over the rooms - never the square
        /// of the whole population. A server that ignores rooms lands on the larger number, and a
        /// server that broadcasts to nobody lands on zero. Both are visible against this without
        /// reading a single screen.
        /// </remarks>
        private int ExpectedSaid()
        {
            Dictionary<string, int> perRoom = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<int, ushort> welcomed in _serverPortFor)
            {
                string room = RoomOf(welcomed.Key);
                int already;
                perRoom.TryGetValue(room, out already);
                perRoom[room] = already + 1;
            }

            int total = 0;
            foreach (KeyValuePair<string, int> room in perRoom)
            {
                total += room.Value * room.Value * _linesEach;
            }

            return total;
        }

        /// <summary>
        /// Gets a value indicating whether the run has finished and reported.
        /// </summary>
        public bool Finished
        {
            get { return _phase == Phase.Done; }
        }

        /// <summary>
        /// Gets how many users the room welcomed.
        /// </summary>
        public int Welcomed
        {
            get { return _welcomed; }
        }

        /// <summary>
        /// Gets how many users the room refused.
        /// </summary>
        public int Rejected
        {
            get { return _rejected; }
        }

        /// <summary>
        /// Takes an arriving frame and, when it is one of ours, decodes the chat message in it.
        /// </summary>
        /// <param name="frame">
        /// The frame that arrived.
        /// </param>
        /// <remarks>
        /// A frame is ours when it came from the room's node AND is addressed to one of the ports
        /// this run opened. The second half matters: the runner may be doing other things on the
        /// same link, and counting somebody else's traffic as chat would make the summary a lie.
        /// </remarks>
        public void OnFrame(XmsgFrame frame)
        {
            if (frame?.Header == null || frame.SubHeader == null)
            {
                return;
            }

            if (frame.Header.SourceNode != _serverNode)
            {
                return;
            }

            ushort ourPort = frame.SubHeader.DestinationPort;
            int user;
            if (!_portToUser.TryGetValue(ourPort, out user))
            {
                return;
            }

            _framesFromRoom++;

            byte[] body = frame.GetBodyBytes();

            // ---- IS THIS XROUT TELLING US THE LETTER FAILED? --------------------------
            // A refused letter comes back as OUR OWN letter with byte 1 - the service number -
            // OVERWRITTEN BY THE RETURN STATUS. CHATSV's own source says so: "byte 1: the
            // service number (XSLET = 65); XROUT overwrites it with the return status on a
            // reply". So a bounce is our serial in byte 0 and something other than XSLET in
            // byte 1.
            //
            // Calling these "undecodable" was WRONG and hid the actual answer. MEASURED
            // 2026-08-18 against a live CHAT-LOBBY: status 30 is XMXRBUS, "Service busy - try
            // later!" - XROUT saying the burst arrived faster than the room could take it.
            // That is the load-test RESULT, and it names the missing client behaviour: retry.
            if (body.Length > 2 && body[0] == LetterSerial && body[1] != XsletService)
            {
                _letterRefused++;
                Console.WriteLine(
                    $"[chatload] user {user}: XROUT refused the letter, status {body[1]}"
                    + $" ({NameForXroutStatus(body[1])})");
                return;
            }

            ChatMessage message;
            if (!ChatMessage.TryDecode(body, out message))
            {
                // NOT silently ignored. A body we cannot read is the single most interesting thing
                // a load test can find - it is what a server under strain would start producing.
                _undecodable++;
                Console.WriteLine(
                    $"[chatload] user {user}: UNDECODABLE body, {body.Length} byte(s): "
                    + Convert.ToHexString(body, 0, Math.Min(body.Length, 24)));
                return;
            }

            switch (message.Kind)
            {
                case ChatMessageKind.Welcome:
                    if (!_serverPortFor.ContainsKey(user))
                    {
                        _welcomed++;
                        _serverPortFor[user] = frame.SubHeader.SourcePort;
                    }

                    break;

                case ChatMessageKind.Reject:
                    _rejected++;
                    Console.WriteLine($"[chatload] user {user} refused: {message.Text}");
                    break;

                case ChatMessageKind.Said:
                    _saidHeard++;
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Drives the run one step.
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

            if (!linkReady || _phase == Phase.Done)
            {
                return;
            }

            // Same gate as every other originated exchange: the link knowing the peer is not
            // enough, the XMSG layer must be able to address it.
            if (!host.ServerHost.OpenLinkFromRememberedSeed(_serverNode))
            {
                return;
            }

            switch (_phase)
            {
                case Phase.Idle:
                    SendAllJoins(host);
                    _phase = Phase.Joining;
                    break;

                case Phase.Joining:
                    // One pump of grace per user for the welcomes to come back, then talk with
                    // whoever got in. Waiting for ALL of them would hang the run on the very case
                    // it is meant to measure - a room that refuses some.
                    _round++;
                    if (_round > _userCount)
                    {
                        Console.WriteLine(
                            $"[chatload] joins settled: {_welcomed} welcomed, {_rejected} refused,"
                            + $" {_userCount - _welcomed - _rejected} silent");
                        _round = 0;
                        _phase = _linesEach > 0 ? Phase.Talking : Phase.Leaving;
                    }

                    break;

                case Phase.Talking:
                    SendOneRoundOfTalk(host);
                    _round++;
                    if (_round >= _linesEach)
                    {
                        _round = 0;
                        _phase = Phase.Settling;
                    }

                    break;

                case Phase.Settling:
                    // WAIT FOR THE ROOM TO SAY IT BACK. A Said is not an answer to the sender - it
                    // is a BROADCAST the room makes afterwards, so it cannot possibly have arrived
                    // by the time the line that caused it has been sent.
                    //
                    // Without this the run went straight from its last line to leaving and then to
                    // printing the summary, and reported "Said messages heard: 0" every single
                    // time - which was read as the messages never being delivered and sent a whole
                    // session looking at frame flags and port numbers. They were being delivered;
                    // the run simply stopped listening first.
                    _round++;
                    if (_round >= SettlePumps)
                    {
                        _round = 0;
                        _phase = Phase.Leaving;
                    }

                    break;

                case Phase.Leaving:
                    SendAllLeaves(host);
                    _round = 0;
                    _phase = Phase.Finishing;
                    break;

                case Phase.Finishing:
                    // The same courtesy at the end: a Left broadcast and the last Said are still
                    // in flight when the leaves go out. Report only once the wire has gone quiet.
                    _round++;
                    if (_round >= SettlePumps)
                    {
                        Report();
                        _phase = Phase.Done;
                    }

                    break;
            }
        }

        /// <summary>
        /// Sends one join letter per simulated user.
        /// </summary>
        /// <param name="host">
        /// The node that stamps the datagram fields.
        /// </param>
        private void SendAllJoins(XmsgNodeHost host)
        {
            Console.WriteLine(
                $"[chatload] {_userCount} user(s) joining {_serviceName} on {_serverName}"
                + $" (node {_serverNode}), {_linesEach} line(s) each,"
                + $" room(s): {string.Join(" ", _rooms)}");

            for (int i = 0; i < _userCount; i++)
            {
                ushort port = host.ServerHost.AllocateSessionPort();
                _ports.Add(port);
                _portToUser[port] = i;

                // THE ROOM RIDES IN THE TEXT FIELD. That is the whole one-port model: the Join
                // already had an empty text field, so naming the room in it changed no byte layout
                // on either side.
                ChatMessage join = new ChatMessage(ChatMessageKind.Join, NameOf(i), RoomOf(i));
                SendLetter(host, port, join);
            }
        }

        /// <summary>
        /// Has every welcomed user say one line.
        /// </summary>
        /// <param name="host">
        /// The node that stamps the datagram fields.
        /// </param>
        private void SendOneRoundOfTalk(XmsgNodeHost host)
        {
            for (int i = 0; i < _ports.Count; i++)
            {
                int user = _portToUser[_ports[i]];
                ushort serverPort;
                if (!_serverPortFor.TryGetValue(user, out serverPort))
                {
                    continue;
                }

                ChatMessage say = new ChatMessage(
                    ChatMessageKind.Say, NameOf(user), "round " + _round + " from " + NameOf(user));
                SendToRoom(host, _ports[i], serverPort, say);
            }
        }

        /// <summary>
        /// Has every welcomed user leave.
        /// </summary>
        /// <param name="host">
        /// The node that stamps the datagram fields.
        /// </param>
        private void SendAllLeaves(XmsgNodeHost host)
        {
            for (int i = 0; i < _ports.Count; i++)
            {
                int user = _portToUser[_ports[i]];
                ushort serverPort;
                if (!_serverPortFor.TryGetValue(user, out serverPort))
                {
                    continue;
                }

                ChatMessage leave = new ChatMessage(
                    ChatMessageKind.Leave, NameOf(user), string.Empty);
                SendToRoom(host, _ports[i], serverPort, leave);
                _leftSent++;
            }
        }

        /// <summary>
        /// Sends a chat message as an XSLET letter, so XROUT finds the room by name.
        /// </summary>
        /// <param name="host">
        /// The node that stamps the datagram fields.
        /// </param>
        /// <param name="ourPort">
        /// The port this user owns, which is also the address the room will answer.
        /// </param>
        /// <param name="message">
        /// The chat message to carry.
        /// </param>
        private void SendLetter(XmsgNodeHost host, ushort ourPort, ChatMessage message)
        {
            XroutMessage letter = XroutRequests.SendLetter(
                serial: 0x7B,                        // 123, the serial CHAT.PLNC uses
                portName: _serviceName,
                systemName: null,                    // the service is on the machine we address
                localAreaOnly: null);

            byte[] head = letter.ToArray();
            byte[] chat = new byte[message.ByteCount];
            message.Encode(chat);

            // The chat bytes go AFTER the parameter blocks. The length word counts only the
            // blocks, so this is the layout CHATSV already reads - see the class remarks.
            byte[] body = new byte[head.Length + chat.Length];
            Buffer.BlockCopy(head, 0, body, 0, head.Length);
            Buffer.BlockCopy(chat, 0, body, head.Length, chat.Length);

            Send(host, clientPort: 0x0000, sourcePort: ourPort, body: body, routed: true);
        }

        /// <summary>
        /// Sends a chat message straight to the room's port, once its address is known.
        /// </summary>
        /// <param name="host">
        /// The node that stamps the datagram fields.
        /// </param>
        /// <param name="ourPort">
        /// The port this user owns.
        /// </param>
        /// <param name="serverPort">
        /// The room's port, learned from the welcome.
        /// </param>
        /// <param name="message">
        /// The chat message to send.
        /// </param>
        /// <remarks>
        /// Everything after the join goes port to port with no name lookup and no letter, which is
        /// what keeps a busy room off XROUT entirely - and is why only the JOIN spends a seat.
        /// </remarks>
        private void SendToRoom(
            XmsgNodeHost host, ushort ourPort, ushort serverPort, ChatMessage message)
        {
            byte[] body = new byte[message.ByteCount];
            message.Encode(body);
            Send(host, clientPort: serverPort, sourcePort: ourPort, body: body, routed: false);
        }

        /// <summary>
        /// Builds and transmits one datagram, and never lets a failure take the node down.
        /// </summary>
        /// <param name="host">
        /// The node that stamps the datagram fields.
        /// </param>
        /// <param name="clientPort">
        /// The destination port, or zero for XROUT.
        /// </param>
        /// <param name="sourcePort">
        /// The port we are sending from.
        /// </param>
        /// <param name="body">
        /// The datagram body.
        /// </param>
        /// <param name="routed">
        /// True for a letter through XROUT, false for a direct port-to-port message.
        /// </param>
        private void Send(
            XmsgNodeHost host, ushort clientPort, ushort sourcePort, byte[] body, bool routed)
        {
            // A LOAD RUN MUST NOT TAKE THE NODE DOWN. It is sending hundreds of frames on purpose,
            // so it is the most likely thing in the runner to hit an edge - and a load test that
            // kills the process it is measuring from proves nothing about the machine.
            try
            {
                // A LETTER AND AN ORDINARY MESSAGE ARE FRAMED DIFFERENTLY, and copying
                // the letter's framing onto the ordinary one is why nothing was ever heard.
                //
                // The letter needs Setup framing and the RoutedLetter option, because it is
                // going to XROUT. An ordinary port-to-port message is not - and the machine
                // itself shows what it should look like. D100's own Welcome came back as:
                //
                //     2113000E4E1F0064047F000A8BD2  21 00 82 84 4E1F 0211 0064 0632 000A  ...
                //                                         ^^ ^^
                //
                // frameFlags 0x82, role 0x84 - the same pair every real FA message on this
                // wire carries, counted across the whole ND-to-ND capture. So the machine
                // frames its ordinary messages that way and ours were framed as Setup
                // letters, which is why the joins worked (those ARE letters) and not one
                // Said was ever heard.
                byte flags = routed
                    ? (byte)XmsgFrameFlags.Setup
                    : (byte)XmsgFrameFlags.ControlBare;

                byte role = routed
                    ? (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.WakeOnStatus
                        | XmsgSendOptions.HighPriority | XmsgSendOptions.RoutedLetter)
                    : (byte)0x84;

                XmsgFrame frame = host.ServerHost.BuildBodyDatagram(
                    _serverNode,
                    _serverNode,
                    clientPort: clientPort,
                    sourcePort: sourcePort,
                    xmcsm: (ushort)body.Length,
                    frameFlags: flags,
                    role: role,
                    body: body,
                    answeredFlags1: XmsgAnsweredFlags1.None);

                byte[] bytes = frame.ToArray();
                host.Transport.Send(new ReadOnlySpan<byte>(bytes));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[chatload] *** send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Prints what the run measured.
        /// </summary>
        /// <remarks>
        /// The numbers are printed even when they are disappointing - a run that says "0 welcomed"
        /// is a result, and a run that quietly says nothing is not.
        /// </remarks>
        private void Report()
        {
            Console.WriteLine("[chatload] ---- result ----");
            Console.WriteLine($"[chatload] users asked to join : {_userCount}");
            Console.WriteLine($"[chatload] welcomed            : {_welcomed}");
            Console.WriteLine($"[chatload] refused by the room : {_rejected}");
            Console.WriteLine($"[chatload] never answered      : {_userCount - _welcomed - _rejected}");
            Console.WriteLine($"[chatload] frames from the room: {_framesFromRoom}");
            int expected = ExpectedSaid();
            Console.WriteLine($"[chatload] Said messages heard : {_saidHeard}");
            Console.WriteLine($"[chatload] Said messages EXPECTED: {expected}   (sum of members^2 x lines, per room)");
            Console.WriteLine($"[chatload] leaves sent         : {_leftSent}");
            Console.WriteLine($"[chatload] letters XROUT refused: {_letterRefused}");
            Console.WriteLine($"[chatload] undecodable bodies  : {_undecodable}");

            // THE VERDICT, SPELLED OUT. A run that prints numbers and leaves the reader to do the
            // arithmetic is how a wrong one gets called a good one - and with rooms the arithmetic
            // is no longer obvious enough to do in your head.
            if (_saidHeard == expected)
            {
                Console.WriteLine("[chatload] ROOMS HELD: every line reached its own room and no other.");
            }
            else if (_saidHeard == 0 && expected > 0)
            {
                Console.WriteLine(
                    "[chatload] *** NOTHING WAS BROADCAST. The server took the lines and sent them"
                    + " to nobody - this is what the broadcast defect of 2026-08-20 looked like.");
            }
            else if (_saidHeard > expected)
            {
                Console.WriteLine(
                    "[chatload] *** ROOMS LEAKED: more Said messages arrived than the rooms account"
                    + " for, so somebody heard another room's conversation.");
            }
            else
            {
                Console.WriteLine(
                    "[chatload] *** SHORT: fewer Said messages than the rooms account for. Lines"
                    + " were dropped, or a member was not in the room the run thinks.");
            }

            if (_undecodable > 0)
            {
                Console.WriteLine(
                    "[chatload] *** an undecodable body is the finding, not a nuisance - the room"
                    + " sent something we could not read.");
            }

            Console.WriteLine(
                "[chatload] CHECK THE SEATS on the machine: X-C -> LIST-NAMES. Free SPs against"
                + " the room should be back where it started.");
        }

        /// <summary>
        /// Builds the nickname for a simulated user.
        /// </summary>
        /// <param name="index">
        /// Zero-based user number.
        /// </param>
        /// <returns>
        /// A nickname inside the room's sixteen-character limit.
        /// </returns>
        private static string NameOf(int index)
        {
            return "LOAD" + index.ToString("00");
        }

        /// <summary>
        /// Names an XROUT status, for the ones this run can actually provoke.
        /// </summary>
        /// <param name="status">
        /// The status byte, which is the offset from <c>XRXXX</c> (041100B = 16960).
        /// </param>
        /// <returns>
        /// The constant's name and meaning, or a note that it is not one we have named.
        /// </returns>
        /// <remarks>
        /// From the shipped <c>XMP-B02:DEFS</c>, where the library form is <c>XRXXX + n</c> - so
        /// status 30 is <c>XMXRBUS</c> = 16990. Only statuses this run has actually seen or can
        /// obviously cause are named; inventing the rest would be a table nobody had checked.
        /// </remarks>
        private static string NameForXroutStatus(int status)
        {
            switch (status)
            {
                case 2: return "XRUNN, unknown name - is the room running?";
                case 3: return "XRDDF, another port already has this name";
                case 30: return "XRBUS, service busy - try later";
                case 34: return "XRMFL, not enough message table space in remote system";
                default: return "not named here - look up XRXXX + " + status;
            }
        }
    }
}
