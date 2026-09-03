using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

namespace RetroXmsg.Hub
{
    /// <summary>
    /// One node joined to the segment: an emulated machine, an observer, or another hub.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A member has a reading thread and a sending thread of its own, with a bounded queue between
    /// the hub and the socket. That separation is the whole point: a member that stops reading
    /// fills its own queue and starts losing frames, and NOBODY ELSE is affected. Sending straight
    /// from the hub's receive path would let one stuck member stall the segment - which is exactly
    /// the failure the UDP backend had.
    /// </para>
    /// <para>
    /// Losing frames when a member falls behind is correct, not a compromise: real Ethernet drops,
    /// and the protocol above retransmits. What is never acceptable is dropping SILENTLY, so every
    /// discard is counted by the hub.
    /// </para>
    /// </remarks>
    internal sealed class Member
    {
        private readonly HubServer _hub;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly bool _isUplink;

        private readonly BlockingCollection<Outgoing> _queue =
            new BlockingCollection<Outgoing>(HubServer.MemberQueueDepth);

        private readonly ManualResetEventSlim _closed = new ManualResetEventSlim(false);

        private Thread? _reader;
        private Thread? _sender;
        private volatile bool _stopping;

        /// <summary>
        /// Initialises a member around an already-handshaken connection.
        /// </summary>
        /// <param name="hub">
        /// The hub this member belongs to.
        /// </param>
        /// <param name="client">
        /// The connected socket.
        /// </param>
        /// <param name="stream">
        /// Its stream.
        /// </param>
        /// <param name="role">
        /// What the peer announced itself as.
        /// </param>
        /// <param name="isUplink">
        /// True when this is our outgoing connection to another hub.
        /// </param>
        public Member(HubServer hub, TcpClient client, NetworkStream stream, byte role, bool isUplink)
        {
            _hub = hub;
            _client = client;
            _stream = stream;
            _isUplink = isUplink;
            Role = role;

            try
            {
                Name = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            }
            catch (Exception)
            {
                Name = "unknown";
            }

            if (isUplink)
            {
                Name = "uplink " + Name;
            }
        }

        /// <summary>
        /// Gets what the peer announced itself as - machine or hub.
        /// </summary>
        public byte Role { get; }

        /// <summary>
        /// Gets a readable name for logs.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Starts the reading and sending threads.
        /// </summary>
        public void Start()
        {
            _reader = new Thread(ReadLoop) { Name = "XmsgHubRead", IsBackground = true };
            _sender = new Thread(SendLoop) { Name = "XmsgHubSend", IsBackground = true };
            _reader.Start();
            _sender.Start();
        }

        /// <summary>
        /// Queues one frame for this member.
        /// </summary>
        /// <param name="frame">
        /// The frame bytes, without hub markers.
        /// </param>
        /// <param name="length">
        /// The number of valid bytes.
        /// </param>
        /// <param name="ttl">
        /// Remaining hub hops, used only when this member is another hub.
        /// </param>
        /// <param name="originId">
        /// The originating hub, used only when this member is another hub.
        /// </param>
        /// <returns>
        /// True when queued; false when this member has fallen behind and the frame was dropped.
        /// </returns>
        public bool Enqueue(byte[] frame, int length, byte ttl, uint originId)
        {
            if (_stopping)
            {
                return false;
            }

            byte[] copy = new byte[length];
            Buffer.BlockCopy(frame, 0, copy, 0, length);

            // TryAdd with no timeout: a full queue means this member is behind, and waiting here
            // would be the hub blocking on one slow member - the thing this design forbids.
            return _queue.TryAdd(new Outgoing(copy, ttl, originId));
        }

        /// <summary>
        /// Blocks until this member's connection has ended.
        /// </summary>
        public void WaitForClose()
        {
            _closed.Wait();
        }

        /// <summary>
        /// Disconnects the member.
        /// </summary>
        public void Close()
        {
            _stopping = true;
            try { _queue.CompleteAdding(); } catch (Exception) { }
            try { _stream.Close(); } catch (Exception) { }
            try { _client.Close(); } catch (Exception) { }
            _closed.Set();
        }

        /// <summary>
        /// Reads frames from this member and hands them to the hub.
        /// </summary>
        private void ReadLoop()
        {
            byte[] prefix = new byte[2];
            byte[] body = new byte[HubServer.MaxFrameLength];

            while (!_stopping)
            {
                if (!HubServer.ReadExactly(_stream, prefix, 2))
                {
                    break;
                }

                int declared = (prefix[0] << 8) | prefix[1];
                if (declared <= 0 || declared > HubServer.MaxFrameLength)
                {
                    break;   // a length this wrong means the stream is out of step; drop the member
                }

                if (!HubServer.ReadExactly(_stream, body, declared))
                {
                    break;
                }

                byte ttl = HubServer.InitialTtl;
                uint originId = _hub.OriginId;
                int frameOffset = 0;
                int frameLength = declared;

                if (Role == HubServer.RoleHub)
                {
                    // From another hub the unit is [ttl][originId(4)][frame].
                    if (declared < 5)
                    {
                        break;
                    }

                    ttl = body[0];
                    originId = ((uint)body[1] << 24) | ((uint)body[2] << 16)
                             | ((uint)body[3] << 8) | body[4];
                    frameOffset = 5;
                    frameLength = declared - 5;
                }

                if (frameLength <= 0)
                {
                    continue;
                }

                byte[] frame;
                if (frameOffset == 0)
                {
                    frame = body;
                }
                else
                {
                    frame = new byte[frameLength];
                    Buffer.BlockCopy(body, frameOffset, frame, 0, frameLength);
                }

                _hub.Repeat(this, frame, frameLength, ttl, originId);
            }

            Close();
            _hub.RemoveMember(this);
        }

        /// <summary>
        /// Writes queued frames to this member's socket.
        /// </summary>
        private void SendLoop()
        {
            byte[] scratch = new byte[HubServer.MaxFrameLength + 7];

            while (!_stopping)
            {
                Outgoing item;
                try
                {
                    if (!_queue.TryTake(out item, 500))
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    break;   // queue completed while we waited
                }

                int at = 0;
                int payloadLength = item.Frame.Length + (Role == HubServer.RoleHub ? 5 : 0);

                scratch[at++] = (byte)(payloadLength >> 8);
                scratch[at++] = (byte)(payloadLength & 0xFF);

                if (Role == HubServer.RoleHub)
                {
                    // Only hub-to-hub links carry the loop markers; a machine never sees them.
                    scratch[at++] = item.Ttl;
                    scratch[at++] = (byte)(item.OriginId >> 24);
                    scratch[at++] = (byte)(item.OriginId >> 16);
                    scratch[at++] = (byte)(item.OriginId >> 8);
                    scratch[at++] = (byte)item.OriginId;
                }

                Buffer.BlockCopy(item.Frame, 0, scratch, at, item.Frame.Length);
                at += item.Frame.Length;

                try
                {
                    _stream.Write(scratch, 0, at);
                    _stream.Flush();
                }
                catch (Exception)
                {
                    break;
                }
            }

            Close();
        }

        /// <summary>
        /// A frame waiting to be written to one member.
        /// </summary>
        private readonly struct Outgoing
        {
            /// <summary>
            /// Initialises the item.
            /// </summary>
            /// <param name="frame">
            /// The frame bytes.
            /// </param>
            /// <param name="ttl">
            /// Remaining hub hops.
            /// </param>
            /// <param name="originId">
            /// The originating hub.
            /// </param>
            public Outgoing(byte[] frame, byte ttl, uint originId)
            {
                Frame = frame;
                Ttl = ttl;
                OriginId = originId;
            }

            /// <summary>
            /// Gets the frame bytes.
            /// </summary>
            public byte[] Frame { get; }

            /// <summary>
            /// Gets the remaining hub hops.
            /// </summary>
            public byte Ttl { get; }

            /// <summary>
            /// Gets the originating hub id.
            /// </summary>
            public uint OriginId { get; }
        }
    }
}
