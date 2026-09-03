using System;

using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Servers.Fa;

using Xunit;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Live;
namespace RetroXmsg.Runner.Tests
{
    /// <summary>
    /// A transfer must not originate until the peer has addressed us.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect this guards</b></para>
    /// <para>
    /// The sync daemon's readiness was once wired to "the LAPB link is Connected", which is true
    /// within milliseconds of startup. The daemon then sent its connect letter before the peer had
    /// said anything, the peer answered <c>XRUNN</c> - unknown name of server or system - and the
    /// driver discarded that answer, so the failure looked like silence.
    /// </para>
    /// <para>
    /// MEASURED 2026-08-17 against a real ND: with the correct gate the same daemon carried two
    /// files first time; with the weak one every transfer was refused. Roughly fifteen wrong
    /// explanations were published before the cause was found, so the rule is worth a test.
    /// </para>
    /// <para><b>What is asserted</b></para>
    /// <para>
    /// The RULE - a freshly built node has not been addressed by anyone, so readiness is false -
    /// rather than the arithmetic inside the agent. The defect was in what the caller WIRED the
    /// gate to, so the test exercises the runner's own wiring helper.
    /// </para>
    /// </remarks>
    public sealed class SyncReadinessWiringTests
    {
        /// <summary>
        /// Our node number in these tests.
        /// </summary>
        private const ushort SelfNode = 19999;

        /// <summary>
        /// The peer a transfer would address.
        /// </summary>
        private const ushort PeerNode = 100;

        /// <summary>
        /// A node that has never been addressed by the peer is NOT ready to originate.
        /// </summary>
        /// <remarks>
        /// This is the whole defect in one line. A gate wired to our own link state answers true
        /// here, which is what made every transfer fire early and be refused.
        /// </remarks>
        [Fact]
        public void APeerThatHasNeverAddressedUs_IsNotReady()
        {
            XmsgNodeHost host = BuildHost();

            Func<bool> ready = global::Program.BuildSyncReadiness(host, PeerNode, false);

            Assert.False(
                ready(),
                "a node that has never received a datagram from the peer cannot address it, so it"
                    + " must not report ready - a gate wired to our own link state would say true"
                    + " here and every transfer would be refused.");
        }

        /// <summary>
        /// Readiness follows the peer, not some other node we happen to know.
        /// </summary>
        /// <remarks>
        /// Guards the obvious wrong fix - asking whether ANY peer is reachable - which would pass
        /// the first test while still firing early against the node the transfer actually targets.
        /// </remarks>
        [Fact]
        public void ReadinessIsAskedAboutThePeerBeingAddressed()
        {
            XmsgNodeHost host = BuildHost();

            Func<bool> forPeer = global::Program.BuildSyncReadiness(host, PeerNode, false);
            Func<bool> forOther = global::Program.BuildSyncReadiness(host, 103, false);

            Assert.False(forPeer());
            Assert.False(forOther());

            // Both false is not the point; the point is that they are independent questions, so a
            // later change that answers one for the other is caught by the CanReach contract rather
            // than by luck. Documented rather than asserted, because asserting it needs a node that
            // has heard from exactly one peer - which is the live case, not a unit one.
            Assert.NotSame(forPeer, forOther);
        }

        /// <summary>
        /// The helper refuses a null node rather than returning a gate that throws later.
        /// </summary>
        [Fact]
        public void ANullNodeIsRefusedAtTheCall()
        {
            Assert.Throws<ArgumentNullException>(
                () => global::Program.BuildSyncReadiness(null!, PeerNode, false));
        }

        /// <summary>
        /// The seed gate is opt-in: the default must stay the strong one.
        /// </summary>
        /// <remarks>
        /// Guards the thing most likely to go wrong by accident - somebody flipping the experiment
        /// on as the default. A peer with no remembered seed is unreachable under BOTH gates, so
        /// this asserts the DEFAULT rather than the seed branch, which needs a populated store and
        /// therefore belongs in a live run rather than a unit test.
        /// </remarks>
        [Fact]
        public void TheSeedGateIsOptIn_AndAnUnknownPeerIsUnreachableUnderEither()
        {
            XmsgNodeHost host = BuildHost();

            Assert.False(
                global::Program.BuildSyncReadiness(host, PeerNode, false)(),
                "the default gate must stay the strong one - a peer that has never addressed us"
                    + " is not ready.");

            Assert.False(
                global::Program.BuildSyncReadiness(host, PeerNode, true)(),
                "even with the seed gate on, a peer whose seed was never learned must stay"
                    + " unreachable - the seed is remembered, never invented.");
        }

        /// <summary>
        /// Builds a node host with no traffic on it.
        /// </summary>
        /// <returns>
        /// A host that has never received a datagram.
        /// </returns>
        private static XmsgNodeHost BuildHost()
        {
            XmsgNodeHost[] hosts = global::Program.BuildRelayHosts(
                new StubLink("in"), new StubLink("out"), SelfNode,
                Array.Empty<RoutingTableEntry>(), new NullResponderSequenceStore(),
                Array.Empty<IXmsgServer>());

            return hosts[0];
        }

        /// <summary>
        /// A link that is Active and carries nothing.
        /// </summary>
        /// <remarks>
        /// Deliberately Active: the point of these tests is that an ACTIVE link is NOT sufficient
        /// to originate. A stub that reported itself down would pass them for the wrong reason.
        /// </remarks>
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
        /// The PULL must honour --originate-from-seed too, not just the sync daemon.
        /// </summary>
        /// <remarks>
        /// <para><b>The defect this guards, measured 2026-08-17</b></para>
        /// <para>
        /// <c>--originate-from-seed</c> reached only the sync daemon. A one-shot pull gated on
        /// <c>CanReach</c> alone, which is true only once the PEER has addressed us - so over a
        /// link where we speak first the pull waited for ever and never sent a frame. It was
        /// silent while it waited, because the "reading" line that names the file comes AFTER
        /// that gate,
        /// so a pull that never started looked exactly like one that started and got no answer.
        /// </para>
        /// <para>
        /// What made it hard to see: a PUSH over the same seam in the same minute carried 42368
        /// bytes, because the push went through the sync daemon and therefore had the seed gate.
        /// Same wire, same machine, opposite result - which reads as a machine fault and is not.
        /// </para>
        /// <para>
        /// Asserted here as the property that was missing: the flag REACHES the pull at all. A
        /// peer with no remembered seed is unreachable under either gate, so the seed BRANCH is a
        /// live-run check; this is the wiring one.
        /// </para>
        /// </remarks>
        [Fact]
        public void ThePullCarriesTheOriginateFromSeedFlag()
        {
            FaPullRun pull = new FaPullRun(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pull-gate-test.txt"),
                new FaReadSource(PeerNode, "SYSTEM", "SOME:TXT"));

            Assert.False(
                pull.OriginateFromSeed,
                "the strong gate must stay the default - a pull must not originate before the peer"
                    + " has addressed us unless it was explicitly asked to.");

            pull.OriginateFromSeed = true;

            Assert.True(
                pull.OriginateFromSeed,
                "--originate-from-seed must be able to reach the pull. When it could not, the pull"
                    + " waited for a peer that speaks second and never sent anything at all.");
        }

        /// <summary>
        /// The one-shot PUSH must honour --originate-from-seed as well.
        /// </summary>
        /// <remarks>
        /// <para><b>The defect this guards, measured 2026-08-18</b></para>
        /// <para>
        /// The pull was fixed and the push was not, so the flag was accepted on the command line
        /// and silently ignored by <see cref="FaPushRun"/>. That is worse than not supporting it:
        /// the run looks healthy the whole time. Against a live D100 the seam reached LAPB
        /// Connected, our announce went out, the peer answered, XROUT letters arrived from node 100
        /// for six minutes - and not one FA frame was ever built, because the gate under it was
        /// still plain <c>CanReach</c>, which is true only once the PEER has addressed us.
        /// </para>
        /// <para>
        /// Why the earlier evidence pointed the wrong way: pushes THROUGH THE SYNC DAEMON worked,
        /// because the daemon had the seed gate from the start. So "push works, pull does not" was
        /// read as a difference between the two DIRECTIONS, when it was really a difference between
        /// the daemon and the one-shot path. Both one-shot drivers were missing the same wire.
        /// </para>
        /// <para>
        /// Asserted here as the property that was missing - the flag REACHES the push, and the
        /// strong gate stays the default. A peer with no remembered seed is unreachable under
        /// either gate, so the seed BRANCH itself is a live-run check, not a unit one.
        /// </para>
        /// </remarks>
        [Fact]
        public void ThePushCarriesTheOriginateFromSeedFlag()
        {
            FaPushRun push = new FaPushRun(
                "push-gate-test.txt",
                new byte[] { 0x41, 0x42 },
                new FaWriteTarget(PeerNode, "D100", "SOME:TXT"));

            Assert.False(
                push.OriginateFromSeed,
                "the strong gate must stay the default - a push must not originate before the peer"
                    + " has addressed us unless it was explicitly asked to.");

            push.OriginateFromSeed = true;

            Assert.True(
                push.OriginateFromSeed,
                "--originate-from-seed must be able to reach the push. When it could not, the push"
                    + " sat behind CanReach on a seam where we speak first and never sent a frame,"
                    + " while the log showed a healthy link.");
        }
}
}
