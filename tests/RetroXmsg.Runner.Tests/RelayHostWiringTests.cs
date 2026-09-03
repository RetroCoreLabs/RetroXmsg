using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Services;
using Xunit;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Live;
namespace RetroXmsg.Runner.Tests
{
    /// <summary>
    /// Pins that a relay registers its servers on BOTH links, which is the defect that made every
    /// request D100 addressed to us on 2026-08-08 die in silence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The relay was built with the servers on the inbound host only. Forwarding was unaffected -
    /// transit never touches a server - so the relay looked healthy and its counters kept moving
    /// while anything addressed TO US over the outbound link got a bare acknowledgement and nothing
    /// more. On the terminal that is a hang, then "NO ANSWER FROM REMOTE SYSTEM; FILE-ACCESS
    /// CONNECTION ABORTED". See <c>DOC/captures/FA-OPERATIONS-2026-08-08</c>.
    /// </para>
    /// <para>
    /// The test asks the question the defect answers wrongly - "does this host know about any
    /// server?" - of each host in turn, rather than of the pair as a whole. Asking about the pair
    /// is what the old code effectively did, and it passed.
    /// </para>
    /// </remarks>
    public sealed class RelayHostWiringTests
    {
        /// <summary>
        /// Our node number in these tests.
        /// </summary>
        private const ushort SelfNode = 19999;

        /// <summary>
        /// Every host a relay builds carries the servers, not just the first one.
        /// </summary>
        [Fact]
        public void BothRelayHosts_HaveTheServersRegistered()
        {
            RecordingServer server = new RecordingServer();
            XmsgNodeHost[] hosts = global::Program.BuildRelayHosts(
                new StubLink("in"), new StubLink("out"), SelfNode,
                Array.Empty<RoutingTableEntry>(), new NullResponderSequenceStore(),
                new IXmsgServer[] { server });

            Assert.Equal(2, hosts.Length);

            for (int i = 0; i < hosts.Length; i++)
            {
                IReadOnlyList<XmsgServerInfo> directory = hosts[i].ServerHost.DescribeServers();

                bool found = false;
                for (int j = 0; j < directory.Count; j++)
                {
                    if (string.Equals(directory[j].Name, RecordingServer.ServerName, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                Assert.True(found, $"host {i} has no {RecordingServer.ServerName} registered");
            }
        }

        /// <summary>
        /// A link that does nothing but exist, so a host can be constructed.
        /// </summary>
        private sealed class StubLink : ILink
        {
            /// <summary>
            /// Initialises the stub in the Active state.
            /// </summary>
            /// <param name="name">
            /// The link name.
            /// </param>
            public StubLink(string name)
            {
                Name = name;
                Status = LinkStatus.Active;
            }

            /// <inheritdoc />
            public event LinkPayloadReceived? PayloadReceived;

            /// <inheritdoc />
            public event LinkStatusChanged? StatusChanged;

            /// <inheritdoc />
            public string Name { get; }

            /// <inheritdoc />
            public LinkStatus Status { get; private set; }

            /// <inheritdoc />
            public bool Start()
            {
                return true;
            }

            /// <inheritdoc />
            public void Stop()
            {
                Status = LinkStatus.Stopped;
            }

            /// <inheritdoc />
            public void Dispose()
            {
                Stop();
            }

            /// <inheritdoc />
            public bool SendData(ReadOnlySpan<byte> payload)
            {
                return true;
            }

            /// <summary>
            /// Keeps the compiler from warning that the events are never raised. Never called.
            /// </summary>
            private void Unused()
            {
                PayloadReceived?.Invoke(this, Array.Empty<byte>(), 0);
                StatusChanged?.Invoke(this, LinkStatus.Stopped, LinkStatus.Active, "unused");
            }
        }

        /// <summary>
        /// A server that answers nothing; only its NAME is under test.
        /// </summary>
        private sealed class RecordingServer : IXmsgServer
        {
            /// <summary>
            /// The name the host's directory should report.
            /// </summary>
            public const string ServerName = "*TEST-SERVER";

            /// <inheritdoc />
            public string Name
            {
                get { return ServerName; }
            }

            /// <inheritdoc />
            public int LogicalPort
            {
                get { return 9; }
            }

            /// <inheritdoc />
            public ushort WirePort
            {
                get { return 0x04D7; }
            }

            /// <inheritdoc />
            public int SessionCount
            {
                get { return 0; }
            }

            /// <inheritdoc />
            public int SessionCapacity
            {
                get { return 0; }
            }

            /// <inheritdoc />
            public bool AdvancesOutputOnAck
            {
                get { return false; }
            }

            /// <inheritdoc />
            public void NotifyAck(ushort remoteNode, ushort ackedFlags1)
            {
            }

            /// <inheritdoc />
            public bool OwnsPort(ushort port)
            {
                return false;
            }

            /// <inheritdoc />
            public IReadOnlyList<XmsgFrame> Handle(XmsgFrame incoming, IXmsgServerTransport transport)
            {
                return Array.Empty<XmsgFrame>();
            }

            /// <inheritdoc />
            public IReadOnlyList<XmsgFrame> DrainPending(IXmsgServerTransport transport)
            {
                return Array.Empty<XmsgFrame>();
            }
        }
    }
}
