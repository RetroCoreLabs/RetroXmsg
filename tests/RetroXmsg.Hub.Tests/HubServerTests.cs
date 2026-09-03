using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

using RetroXmsg.Hub;

using Xunit;

namespace RetroXmsg.Hub.Tests
{
    /// <summary>
    /// Gate for the virtual Ethernet segment: a frame from one member reaches every other member
    /// and never the sender, a member that stops reading cannot stall the others, and a hub refuses
    /// a frame that has been round a loop.
    /// </summary>
    public sealed class HubServerTests
    {
        /// <summary>
        /// Handshake magic 'R' 'E' 'T' 'H'.
        /// </summary>
        private static readonly byte[] Magic = { 0x52, 0x45, 0x54, 0x48 };

        /// <summary>
        /// A test client that joins a hub the same way a machine does.
        /// </summary>
        private sealed class TestMember : IDisposable
        {
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;

            /// <summary>
            /// Connects and completes the handshake.
            /// </summary>
            /// <param name="port">
            /// The hub's port.
            /// </param>
            /// <param name="role">
            /// The role byte to announce (1 machine, 2 hub).
            /// </param>
            public TestMember(int port, byte role)
            {
                _client = new TcpClient();
                _client.Connect("127.0.0.1", port);
                _client.NoDelay = true;
                _stream = _client.GetStream();

                byte[] hello = { Magic[0], Magic[1], Magic[2], Magic[3], role };
                _stream.Write(hello, 0, hello.Length);
                _stream.Flush();

                byte[] peer = new byte[5];
                ReadExactly(peer, 5);
                Role = role;
            }

            /// <summary>
            /// Gets the role this member announced.
            /// </summary>
            public byte Role { get; }

            /// <summary>
            /// Sends one frame with the length prefix.
            /// </summary>
            /// <param name="payload">
            /// The bytes to send.
            /// </param>
            public void Send(byte[] payload)
            {
                byte[] buffer = new byte[2 + payload.Length];
                buffer[0] = (byte)(payload.Length >> 8);
                buffer[1] = (byte)(payload.Length & 0xFF);
                Buffer.BlockCopy(payload, 0, buffer, 2, payload.Length);
                _stream.Write(buffer, 0, buffer.Length);
                _stream.Flush();
            }

            /// <summary>
            /// Reads one frame, or returns null when nothing arrives in time.
            /// </summary>
            /// <param name="timeoutMs">
            /// How long to wait.
            /// </param>
            /// <returns>
            /// The frame payload, or null.
            /// </returns>
            public byte[]? Receive(int timeoutMs)
            {
                _client.ReceiveTimeout = timeoutMs;
                byte[] prefix = new byte[2];
                if (!TryReadExactly(prefix, 2))
                {
                    return null;
                }

                int length = (prefix[0] << 8) | prefix[1];
                byte[] body = new byte[length];
                return TryReadExactly(body, length) ? body : null;
            }

            /// <inheritdoc />
            public void Dispose()
            {
                try { _stream.Dispose(); } catch (Exception) { }
                try { _client.Dispose(); } catch (Exception) { }
            }

            private void ReadExactly(byte[] buffer, int count)
            {
                if (!TryReadExactly(buffer, count))
                {
                    throw new InvalidOperationException("handshake did not complete");
                }
            }

            private bool TryReadExactly(byte[] buffer, int count)
            {
                int got = 0;
                while (got < count)
                {
                    int n;
                    try
                    {
                        n = _stream.Read(buffer, got, count - got);
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
        }

        /// <summary>
        /// Builds a frame that looks like ND/COSMOS traffic, with a marker byte to identify it.
        /// </summary>
        /// <param name="marker">
        /// A byte placed in the payload.
        /// </param>
        /// <returns>
        /// The frame bytes.
        /// </returns>
        private static byte[] Frame(byte marker)
        {
            byte[] frame = new byte[64];
            for (int i = 0; i < 6; i++)
            {
                frame[i] = 0xFF;             // broadcast destination
            }

            frame[6] = 0x08;
            frame[7] = 0x00;
            frame[8] = 0x26;                 // ND vendor prefix
            frame[12] = 0x00;
            frame[13] = 0x0E;                // 802.3 LENGTH, not an EtherType
            frame[14] = 0xA8;                // LLC DSAP
            frame[15] = 0xA8;                // LLC SSAP
            frame[16] = 0x03;                // UI
            frame[17] = marker;
            return frame;
        }

        /// <summary>
        /// Builds a full-size frame, used where the point is to move enough bytes to fill a
        /// socket buffer rather than to check the contents.
        /// </summary>
        /// <param name="marker">
        /// A byte placed in the payload.
        /// </param>
        /// <param name="size">
        /// Total frame size in bytes.
        /// </param>
        /// <returns>
        /// The frame bytes.
        /// </returns>
        private static byte[] BigFrame(byte marker, int size)
        {
            byte[] frame = new byte[size];
            for (int i = 0; i < 6; i++)
            {
                frame[i] = 0xFF;
            }

            frame[6] = 0x08;
            frame[7] = 0x00;
            frame[8] = 0x26;
            frame[14] = 0xA8;
            frame[15] = 0xA8;
            frame[16] = 0x03;
            frame[17] = marker;
            return frame;
        }

        [Fact]
        public void FrameReachesEveryOtherMember_ButNotTheSender()
        {
            HubServer hub = new HubServer(0);
            hub.Start();

            try
            {
                using TestMember a = new TestMember(hub.Port, HubServer.RoleMachine);
                using TestMember b = new TestMember(hub.Port, HubServer.RoleMachine);
                using TestMember c = new TestMember(hub.Port, HubServer.RoleMachine);

                Thread.Sleep(200);
                a.Send(Frame(0x5A));

                byte[]? gotB = b.Receive(5000);
                byte[]? gotC = c.Receive(5000);

                Assert.NotNull(gotB);
                Assert.NotNull(gotC);
                Assert.Equal(0x5A, gotB![17]);
                Assert.Equal(0x5A, gotC![17]);

                // A hub never sends a frame back out of the port it arrived on.
                Assert.Null(a.Receive(500));
            }
            finally
            {
                hub.Stop();
            }
        }

        /// <summary>
        /// THE ONE THAT MATTERS. A member that stops reading must not stop the others being served.
        /// The same mistake in the UDP backend - delivering on the receive thread - let one stuck
        /// consumer silently kill all traffic.
        /// </summary>
        [Fact]
        public void SlowMember_DoesNotStallTheOthers()
        {
            HubServer hub = new HubServer(0);
            hub.Start();

            try
            {
                using TestMember sender = new TestMember(hub.Port, HubServer.RoleMachine);
                using TestMember healthy = new TestMember(hub.Port, HubServer.RoleMachine);
                using TestMember stuck = new TestMember(hub.Port, HubServer.RoleMachine);

                Thread.Sleep(200);

                // A member that simply does not call read is NOT yet backed up: the operating
                // system's own receive buffer swallows tens of kilobytes first, and only when THAT
                // is full does our queue start filling. Small frames therefore prove nothing - the
                // first version of this test sent 768 x 64 bytes, which fits in the socket buffer,
                // so nothing ever overflowed and the test failed for the wrong reason.
                //
                // Full-size frames and enough of them push past the socket buffer and into the
                // queue, which is what actually exercises the drop path.
                const int count = 2000;
                const int frameSize = 1500;
                int healthyGot = 0;

                Thread drain = new Thread(() =>
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (healthy.Receive(3000) == null)
                        {
                            break;
                        }

                        healthyGot++;
                    }
                });
                drain.IsBackground = true;
                drain.Start();

                for (int i = 0; i < count; i++)
                {
                    sender.Send(BigFrame((byte)(i & 0xFF), frameSize));
                }

                drain.Join(30000);

                // The healthy member keeps being served even though "stuck" never read a byte.
                Assert.True(
                    healthyGot > HubServer.MemberQueueDepth,
                    $"healthy member only received {healthyGot} of {count} - a stuck member stalled the segment");

                Assert.True(
                    hub.FramesDroppedSlow > 0,
                    $"the stuck member should have overflowed and been counted (dropped {hub.FramesDroppedSlow})");
            }
            finally
            {
                hub.Stop();
            }
        }

        /// <summary>
        /// A frame arriving from another hub carrying THIS hub's own origin id has been round a
        /// circle, and must be dropped rather than repeated forever.
        /// </summary>
        [Fact]
        public void FrameThatHasBeenRoundALoop_IsDropped()
        {
            HubServer hub = new HubServer(0);
            hub.Start();

            try
            {
                using TestMember peerHub = new TestMember(hub.Port, HubServer.RoleHub);
                using TestMember machine = new TestMember(hub.Port, HubServer.RoleMachine);

                Thread.Sleep(200);

                // A hub link's unit is [ttl][originId(4)][frame]. Stamp it with the hub's OWN id.
                byte[] frame = Frame(0x77);
                byte[] unit = new byte[5 + frame.Length];
                unit[0] = HubServer.InitialTtl;
                unit[1] = (byte)(hub.OriginId >> 24);
                unit[2] = (byte)(hub.OriginId >> 16);
                unit[3] = (byte)(hub.OriginId >> 8);
                unit[4] = (byte)hub.OriginId;
                Buffer.BlockCopy(frame, 0, unit, 5, frame.Length);

                peerHub.Send(unit);

                Assert.Null(machine.Receive(1000));
                Assert.True(hub.FramesDroppedLoop > 0, "the looped frame should have been counted as such");
            }
            finally
            {
                hub.Stop();
            }
        }

        /// <summary>
        /// A frame whose hop count has run out is dropped, so a chain of hubs cannot carry it for
        /// ever even if the origin check somehow misses it.
        /// </summary>
        [Fact]
        public void FrameWithNoHopsLeft_IsDropped()
        {
            HubServer hub = new HubServer(0);
            hub.Start();

            try
            {
                using TestMember peerHub = new TestMember(hub.Port, HubServer.RoleHub);
                using TestMember machine = new TestMember(hub.Port, HubServer.RoleMachine);

                Thread.Sleep(200);

                byte[] frame = Frame(0x33);
                byte[] unit = new byte[5 + frame.Length];
                unit[0] = 0;            // no hops left
                unit[1] = 0xDE;         // some other hub's id, so the loop check does not fire
                unit[2] = 0xAD;
                unit[3] = 0xBE;
                unit[4] = 0xEF;
                Buffer.BlockCopy(frame, 0, unit, 5, frame.Length);

                peerHub.Send(unit);

                Assert.Null(machine.Receive(1000));
                Assert.True(hub.FramesDroppedTtl > 0);
            }
            finally
            {
                hub.Stop();
            }
        }

        /// <summary>
        /// A machine's frame reaches a hub member wrapped in the loop markers, and the frame itself
        /// is unchanged - a machine link stays byte-identical to what machines already speak.
        /// </summary>
        [Fact]
        public void FrameToAHubMember_CarriesTheMarkers_AndTheFrameIsUnchanged()
        {
            HubServer hub = new HubServer(0);
            hub.Start();

            try
            {
                using TestMember machine = new TestMember(hub.Port, HubServer.RoleMachine);
                using TestMember peerHub = new TestMember(hub.Port, HubServer.RoleHub);

                Thread.Sleep(200);
                machine.Send(Frame(0x42));

                byte[]? unit = peerHub.Receive(5000);
                Assert.NotNull(unit);
                Assert.True(unit!.Length > 5);

                Assert.Equal(HubServer.InitialTtl - 1, unit[0]);   // one hop consumed

                uint origin = ((uint)unit[1] << 24) | ((uint)unit[2] << 16)
                            | ((uint)unit[3] << 8) | unit[4];
                Assert.Equal(hub.OriginId, origin);

                // The frame after the markers is exactly what the machine sent.
                Assert.Equal(0x42, unit[5 + 17]);
                Assert.Equal(0xA8, unit[5 + 14]);
            }
            finally
            {
                hub.Stop();
            }
        }
    }
}
