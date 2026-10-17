//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroXmsg - Accessing yesterday's Xmsg API with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RetroXmsg.Hub
{
    /// <summary>
    /// Reports a line worth logging.
    /// </summary>
    /// <param name="message">
    /// The message.
    /// </param>
    public delegate void HubLog(string message);

    /// <summary>
    /// A virtual Ethernet segment: every member that joins hears every frame any other member
    /// sends, exactly as a real hub repeats a frame out of every port but the one it arrived on.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b></para>
    /// <para>
    /// A TCP link joins exactly TWO nodes, so it can carry traffic but leaves nowhere to watch
    /// from. Decoding the COSMOS protocol needs a third party on the same segment seeing every
    /// frame, and host-local traffic cannot be captured with Wireshark on this machine because
    /// npcap has no loopback adapter installed. A hub gives both: many machines, plus observers.
    /// </para>
    /// <para><b>Wire format - unchanged for machines</b></para>
    /// <para>
    /// A member speaks exactly what <c>TcpEthernetBackend</c> already speaks, so existing machines
    /// join with no change at all: a 5-byte hello of <c>'R' 'E' 'T' 'H'</c> plus a version byte,
    /// then each frame as a big-endian 2-byte length followed by that many bytes.
    /// </para>
    /// <para><b>Chaining hubs, and why frames need a marker</b></para>
    /// <para>
    /// Hubs can join each other so segments span the internet. That creates a way for a frame to
    /// travel in a circle forever if anyone ever wires two hubs to each other in a loop. Two
    /// cheap markers stop it, carried ONLY on hub-to-hub links so machine links stay untouched:
    /// </para>
    /// <para>
    /// - an ORIGIN ID, a random number chosen by whichever hub first put the frame on a hub link.
    ///   A hub drops any frame coming back with its own id, which kills a circle after one lap.
    /// - a TTL, decremented at each hub. Anything that survives the origin check still dies here.
    /// </para>
    /// <para>
    /// The version byte in the hello says which kind of member is connecting - <c>1</c> for a
    /// machine, <c>2</c> for a hub - so a hub knows whether to add those markers without an extra
    /// round trip, and an old machine that knows nothing about them still works.
    /// </para>
    /// <para><b>A slow member must not stall the segment</b></para>
    /// <para>
    /// Each member has its own bounded queue and its own sending thread. A member that stops
    /// reading fills its queue and then loses frames, and only that member loses them. Nothing on
    /// the receive path ever blocks on a send. This is deliberate: the same mistake in the UDP
    /// backend - delivering on the receive thread - is what let one stuck consumer silently stop
    /// all traffic.
    /// </para>
    /// </remarks>
    public sealed class HubServer
    {
        /// <summary>
        /// Handshake magic: 'R' 'E' 'T' 'H'.
        /// </summary>
        private static readonly byte[] Magic = { 0x52, 0x45, 0x54, 0x48 };

        /// <summary>
        /// Hello version announcing an emulated machine (what a machine already sends).
        /// </summary>
        public const byte RoleMachine = 0x01;

        /// <summary>
        /// Hello version announcing another hub, whose frames carry the loop markers.
        /// </summary>
        public const byte RoleHub = 0x02;

        /// <summary>
        /// Largest frame carried, matching the transport it interoperates with.
        /// </summary>
        public const int MaxFrameLength = 2048;

        /// <summary>
        /// Frames a member may fall behind by before it starts losing them.
        /// </summary>
        public const int MemberQueueDepth = 256;

        /// <summary>
        /// Hub hops a frame may make before it is discarded.
        /// </summary>
        public const byte InitialTtl = 8;

        private readonly int _listenPort;
        private readonly string? _uplinkHost;
        private readonly int _uplinkPort;
        private readonly uint _originId;

        private readonly List<Member> _members = new List<Member>();
        private readonly object _membersLock = new object();

        private TcpListener? _listener;
        private Thread? _acceptThread;
        private Thread? _uplinkThread;
        private volatile bool _stopRequested;

        private long _framesIn;
        private long _framesForwarded;
        private long _framesDroppedSlow;
        private long _framesDroppedLoop;
        private long _framesDroppedTtl;

        /// <summary>
        /// Where every repeated frame is stored, or null when capture is off.
        /// </summary>
        /// <remarks>
        /// Set through <see cref="StartCapture"/> before the hub is started. Frames are written in
        /// <see cref="Repeat"/>, which every frame passes through exactly once - a frame is stored
        /// as it ARRIVES, before the loop and TTL checks drop it, because a frame that got dropped
        /// is often the one worth looking at.
        /// </remarks>
        private PcapWriter? _capture;

        /// <summary>
        /// Occurs when the hub has something worth printing.
        /// </summary>
        public event HubLog? Log;

        /// <summary>
        /// Initialises the hub.
        /// </summary>
        /// <param name="listenPort">
        /// The TCP port members connect to. Zero picks a free port; negative means do not listen at
        /// all, which is only useful for a hub that exists purely to join another one.
        /// </param>
        /// <param name="uplinkHost">
        /// A remote hub to join as a member, or null for a hub that is the root.
        /// </param>
        /// <param name="uplinkPort">
        /// The remote hub's port. Ignored when <paramref name="uplinkHost"/> is null.
        /// </param>
        public HubServer(int listenPort, string? uplinkHost = null, int uplinkPort = 0)
        {
            _listenPort = listenPort;
            _uplinkHost = uplinkHost;
            _uplinkPort = uplinkPort;

            // Identifies frames this hub has already put on a hub link. Random so two hubs that
            // have never met cannot pick the same one; it means nothing beyond "this hub".
            _originId = (uint)Environment.TickCount ^ (uint)(Guid.NewGuid().GetHashCode());
        }

        /// <summary>
        /// Starts storing every frame into a pcap file.
        /// </summary>
        /// <remarks>
        /// Call before <c>Start</c>. The file is classic pcap with link type Ethernet, so
        /// Wireshark and the dissectors in this repository read it directly.
        /// </remarks>
        /// <param name="path">
        /// Where to write. An existing file is replaced.
        /// </param>
        /// <exception cref="IOException">
        /// Thrown when the file cannot be created.
        /// </exception>
        public void StartCapture(string path)
        {
            PcapWriter? previous = _capture;
            _capture = new PcapWriter(path);
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        /// <summary>
        /// Gets how many frames the capture has stored, or zero when capture is off.
        /// </summary>
        public long FramesCaptured
        {
            get
            {
                PcapWriter? capture = _capture;
                return capture == null ? 0 : capture.FramesWritten;
            }
        }

        /// <summary>
        /// Gets the port actually bound, once started.
        /// </summary>
        public int Port { get; private set; }

        /// <summary>
        /// Gets this hub's origin id, the value it refuses to accept back.
        /// </summary>
        public uint OriginId
        {
            get { return _originId; }
        }

        /// <summary>
        /// Gets a short description of what this hub is.
        /// </summary>
        public string Description
        {
            get
            {
                return _uplinkHost == null
                    ? $"hub:{Port}"
                    : $"hub:{Port} uplink {_uplinkHost}:{_uplinkPort}";
            }
        }

        /// <summary>
        /// Gets the number of members currently joined.
        /// </summary>
        public int MemberCount
        {
            get { lock (_membersLock) { return _members.Count; } }
        }

        /// <summary>
        /// Gets how many of the members are machines rather than other hubs.
        /// </summary>
        public int MachineMemberCount
        {
            get
            {
                lock (_membersLock)
                {
                    int n = 0;
                    for (int i = 0; i < _members.Count; i++)
                    {
                        if (_members[i].Role == RoleMachine)
                        {
                            n++;
                        }
                    }

                    return n;
                }
            }
        }

        /// <summary>
        /// Gets the number of frames received from members.
        /// </summary>
        public long FramesIn
        {
            get { return Interlocked.Read(ref _framesIn); }
        }

        /// <summary>
        /// Gets the number of frame copies sent on to other members.
        /// </summary>
        public long FramesForwarded
        {
            get { return Interlocked.Read(ref _framesForwarded); }
        }

        /// <summary>
        /// Gets the number of copies dropped because a member had fallen behind.
        /// </summary>
        public long FramesDroppedSlow
        {
            get { return Interlocked.Read(ref _framesDroppedSlow); }
        }

        /// <summary>
        /// Gets the number of frames dropped because they had been round a loop.
        /// </summary>
        public long FramesDroppedLoop
        {
            get { return Interlocked.Read(ref _framesDroppedLoop); }
        }

        /// <summary>
        /// Gets the number of frames dropped because their hop count ran out.
        /// </summary>
        public long FramesDroppedTtl
        {
            get { return Interlocked.Read(ref _framesDroppedTtl); }
        }

        /// <summary>
        /// Starts listening and, when configured, joins the remote hub.
        /// </summary>
        /// <exception cref="SocketException">
        /// Thrown when the port cannot be bound.
        /// </exception>
        public void Start()
        {
            _stopRequested = false;

            if (_listenPort >= 0)
            {
                _listener = new TcpListener(IPAddress.Any, _listenPort);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

                _acceptThread = new Thread(AcceptLoop) { Name = "XmsgHubAccept", IsBackground = true };
                _acceptThread.Start();
            }

            if (_uplinkHost != null)
            {
                _uplinkThread = new Thread(UplinkLoop) { Name = "XmsgHubUplink", IsBackground = true };
                _uplinkThread.Start();
            }
        }

        /// <summary>
        /// Stops the hub and disconnects every member. Idempotent.
        /// </summary>
        public void Stop()
        {
            _stopRequested = true;

            try { _listener?.Stop(); } catch (Exception) { }
            _listener = null;

            Member[] members;
            lock (_membersLock)
            {
                members = _members.ToArray();
                _members.Clear();
            }

            for (int i = 0; i < members.Length; i++)
            {
                members[i].Close();
            }

            _acceptThread?.Join(2000);
            _uplinkThread?.Join(2000);
            _acceptThread = null;
            _uplinkThread = null;

            // Close the capture LAST, once no member thread can still be writing into it.
            PcapWriter? capture = _capture;
            _capture = null;
            if (capture != null)
            {
                capture.Dispose();
            }
        }

        /// <summary>
        /// Accepts members until stopped.
        /// </summary>
        private void AcceptLoop()
        {
            Raise($"listening on port {Port}");

            while (!_stopRequested)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (_stopRequested)
                    {
                        break;
                    }

                    Thread.Sleep(50);
                    continue;
                }

                AddMember(client, isUplink: false);
            }
        }

        /// <summary>
        /// Keeps a connection to the remote hub, redialling when it drops.
        /// </summary>
        private void UplinkLoop()
        {
            while (!_stopRequested)
            {
                TcpClient client = new TcpClient();
                try
                {
                    client.Connect(_uplinkHost!, _uplinkPort);
                }
                catch (Exception)
                {
                    try { client.Close(); } catch (Exception) { }
                    if (_stopRequested)
                    {
                        break;
                    }

                    Thread.Sleep(2000);   // the remote hub may not be up yet
                    continue;
                }

                Member? member = AddMember(client, isUplink: true);
                if (member == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                // Hold here until that member's reader ends, then redial.
                member.WaitForClose();
                if (!_stopRequested)
                {
                    Raise("uplink dropped, redialling");
                    Thread.Sleep(1000);
                }
            }
        }

        /// <summary>
        /// Completes the handshake on a new connection and starts serving it.
        /// </summary>
        /// <param name="client">
        /// The connected socket.
        /// </param>
        /// <param name="isUplink">
        /// True when this is our outgoing connection to another hub.
        /// </param>
        /// <returns>
        /// The member, or null when the handshake failed.
        /// </returns>
        private Member? AddMember(TcpClient client, bool isUplink)
        {
            NetworkStream stream;
            byte peerRole;

            try
            {
                client.NoDelay = true;
                stream = client.GetStream();

                // We always announce ourselves as a hub. The peer's byte tells us what it is.
                if (!Handshake(stream, RoleHub, out peerRole))
                {
                    try { client.Close(); } catch (Exception) { }
                    Raise("a connection failed the handshake and was dropped");
                    return null;
                }
            }
            catch (Exception)
            {
                try { client.Close(); } catch (Exception) { }
                return null;
            }

            Member member = new Member(this, client, stream, peerRole, isUplink);

            lock (_membersLock)
            {
                _members.Add(member);
            }

            Raise($"member joined: {member.Name} ({(peerRole == RoleHub ? "hub" : "machine")}), {MemberCount} total");
            member.Start();
            return member;
        }

        /// <summary>
        /// Removes a member that has disconnected.
        /// </summary>
        /// <param name="member">
        /// The member to remove.
        /// </param>
        internal void RemoveMember(Member member)
        {
            bool removed;
            lock (_membersLock)
            {
                removed = _members.Remove(member);
            }

            if (removed)
            {
                Raise($"member left: {member.Name}, {MemberCount} remain");
            }
        }

        /// <summary>
        /// Repeats one frame to every member except the one it came from.
        /// </summary>
        /// <param name="from">
        /// The member the frame arrived on.
        /// </param>
        /// <param name="frame">
        /// The frame bytes, without any hub markers.
        /// </param>
        /// <param name="length">
        /// The number of valid bytes.
        /// </param>
        /// <param name="ttl">
        /// Remaining hub hops.
        /// </param>
        /// <param name="originId">
        /// The hub that first put this frame on a hub link, or this hub when it arrived from a
        /// machine.
        /// </param>
        internal void Repeat(Member from, byte[] frame, int length, byte ttl, uint originId)
        {
            Interlocked.Increment(ref _framesIn);

            // Store it BEFORE any of the checks below can drop it. A dropped frame is exactly the
            // kind of thing a capture exists to show, and leaving it out would make the pcap agree
            // with whatever the hub already believes.
            PcapWriter? capture = _capture;
            if (capture != null)
            {
                capture.Write(frame, length);
            }

            if (originId == _originId && from.Role == RoleHub)
            {
                // It has been round a circle and come back to us.
                Interlocked.Increment(ref _framesDroppedLoop);
                return;
            }

            if (ttl == 0)
            {
                Interlocked.Increment(ref _framesDroppedTtl);
                return;
            }

            byte outgoingTtl = (byte)(ttl - 1);

            Member[] targets;
            lock (_membersLock)
            {
                targets = _members.ToArray();
            }

            for (int i = 0; i < targets.Length; i++)
            {
                Member target = targets[i];
                if (ReferenceEquals(target, from))
                {
                    continue;   // never back out of the port it came in on
                }

                if (target.Enqueue(frame, length, outgoingTtl, originId))
                {
                    Interlocked.Increment(ref _framesForwarded);
                }
                else
                {
                    Interlocked.Increment(ref _framesDroppedSlow);
                }
            }
        }

        /// <summary>
        /// Exchanges the 5-byte hello and reads the peer's role.
        /// </summary>
        /// <param name="stream">
        /// The connected stream.
        /// </param>
        /// <param name="ourRole">
        /// The role byte we announce.
        /// </param>
        /// <param name="peerRole">
        /// Receives the peer's role byte.
        /// </param>
        /// <returns>
        /// True when the peer sent the expected magic.
        /// </returns>
        private static bool Handshake(NetworkStream stream, byte ourRole, out byte peerRole)
        {
            peerRole = RoleMachine;

            byte[] hello = new byte[5];
            hello[0] = Magic[0];
            hello[1] = Magic[1];
            hello[2] = Magic[2];
            hello[3] = Magic[3];
            hello[4] = ourRole;

            stream.Write(hello, 0, hello.Length);
            stream.Flush();

            byte[] peer = new byte[5];
            if (!ReadExactly(stream, peer, 5))
            {
                return false;
            }

            for (int i = 0; i < Magic.Length; i++)
            {
                if (peer[i] != Magic[i])
                {
                    return false;
                }
            }

            peerRole = peer[4];
            return true;
        }

        /// <summary>
        /// Reads exactly the requested number of bytes.
        /// </summary>
        /// <param name="stream">
        /// The stream to read.
        /// </param>
        /// <param name="buffer">
        /// The destination.
        /// </param>
        /// <param name="count">
        /// How many bytes are needed.
        /// </param>
        /// <returns>
        /// True when all of them arrived.
        /// </returns>
        internal static bool ReadExactly(NetworkStream stream, byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n;
                try
                {
                    n = stream.Read(buffer, got, count - got);
                }
                catch (Exception)
                {
                    return false;
                }

                if (n <= 0)
                {
                    return false;
                }

                got += n;
            }

            return true;
        }

        /// <summary>
        /// Raises the log event.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        internal void Raise(string message)
        {
            Log?.Invoke(message);
        }
    }
}
