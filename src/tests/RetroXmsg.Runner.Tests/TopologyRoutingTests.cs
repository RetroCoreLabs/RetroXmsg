using System.Collections.Generic;

using RetroXmsg.Runner;

using Xunit;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Live;
namespace RetroXmsg.Runner.Tests
{
    /// <summary>
    /// The routing decision read off the topology: which neighbour reaches which nodes.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is tested and not just read</b></para>
    /// This is what a relaying node registers with <c>DatagramRelay</c>, so getting it wrong sends
    /// a datagram out of the wrong link - or out of none, dropping traffic silently. It is small
    /// enough to look obviously correct and exactly the kind of thing that is quietly wrong.
    /// <para>
    /// The fixture is the live topology from <c>topology-d19999.json</c>: we are D19999 on the
    /// Ethernet segment with D100 and D102 as neighbours, and D103 sitting behind D100 on an HDLC
    /// line.
    /// </para>
    /// </remarks>
    public sealed class TopologyRoutingTests
    {
        /// <summary>
        /// Builds the live topology shape.
        /// </summary>
        /// <returns>
        /// The config.
        /// </returns>
        private static TopologyConfig LiveShape()
        {
            TopologyConfig config = new TopologyConfig();

            config.Nodes = new List<TopologyNode>
            {
                new TopologyNode { Id = 19999, Alias = "d19999", Reach = "local" },
                new TopologyNode { Id = 100, Alias = "d100", Reach = "neighbour", Link = 1 },
                new TopologyNode { Id = 102, Alias = "d102", Reach = "neighbour", Link = 2 },
                new TopologyNode { Id = 103, Alias = "d103", Reach = "via", Via = new[] { 100 } },
            };

            return config;
        }

        /// <summary>
        /// Every declared neighbour is returned, not just the first.
        /// </summary>
        /// <remarks>
        /// <c>PrimaryEthernetPeer</c> deliberately returns one, because a lone host speaks over one
        /// link. A relaying runner needs them all.
        /// </remarks>
        [Fact]
        public void NeighboursReturnsEveryNeighbour()
        {
            List<TopologyNode> neighbours = LiveShape().Neighbours();

            Assert.Equal(2, neighbours.Count);
            Assert.Equal(100, neighbours[0].Id);
            Assert.Equal(102, neighbours[1].Id);
        }

        /// <summary>
        /// A node routed through a neighbour is reachable through that neighbour's link.
        /// </summary>
        [Fact]
        public void NodesReachableThroughFindsTheViaNode()
        {
            List<ushort> throughD100 = LiveShape().NodesReachableThrough(100);

            Assert.Equal(new ushort[] { 103 }, throughD100);
        }

        /// <summary>
        /// A neighbour nothing is routed through gets an empty list, not everything.
        /// </summary>
        /// <remarks>
        /// The failure that matters: if this returned every via node regardless of chain, D103's
        /// traffic would be registered on BOTH links and the relay would send it to whichever was
        /// added last - a silent, intermittent misroute.
        /// </remarks>
        [Fact]
        public void NodesReachableThroughIsEmptyForANeighbourNothingRoutesVia()
        {
            Assert.Empty(LiveShape().NodesReachableThrough(102));
        }

        /// <summary>
        /// Only the FIRST hop of a via chain decides which of our links to use.
        /// </summary>
        /// <remarks>
        /// A chain <c>via: [100, 42]</c> means "through 100, then through 42". This node only picks
        /// one of its own links, and that is settled by the first entry; the remaining hops are the
        /// next relay's decision. The captured route-through behaves exactly this way - each hop
        /// re-marks the datagram and passes the original endpoints on untouched.
        /// </remarks>
        [Fact]
        public void OnlyTheFirstHopOfAChainDecidesTheLink()
        {
            TopologyConfig config = LiveShape();
            config.Nodes.Add(new TopologyNode { Id = 42, Reach = "via", Via = new[] { 100, 7 } });

            Assert.Equal(new ushort[] { 103, 42 }, config.NodesReachableThrough(100));

            // Not registered on the SECOND hop's number - 7 is not one of our links.
            Assert.Empty(config.NodesReachableThrough(7));
        }

        /// <summary>
        /// A via node with no chain is skipped rather than routed somewhere arbitrary.
        /// </summary>
        /// <remarks>
        /// A malformed entry must not silently acquire a route. Dropping it means the datagram is
        /// reported unroutable, which is visible; guessing a link would be an invisible misroute.
        /// </remarks>
        [Fact]
        public void AViaNodeWithNoChainIsNotRoutedAnywhere()
        {
            TopologyConfig config = LiveShape();
            config.Nodes.Add(new TopologyNode { Id = 55, Reach = "via", Via = null });
            config.Nodes.Add(new TopologyNode { Id = 56, Reach = "via", Via = new int[0] });

            List<ushort> throughD100 = config.NodesReachableThrough(100);

            Assert.DoesNotContain((ushort)55, throughD100);
            Assert.DoesNotContain((ushort)56, throughD100);
        }
    }
}
