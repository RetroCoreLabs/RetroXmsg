using RetroXmsg.Runner;
using RetroIO.Protocols.Xmsg.Node.Seam;

using Xunit;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Live;
namespace RetroXmsg.Runner.Tests
{
    /// <summary>
    /// Pins the announce timing rule that two live defects came from, and the two deliberate
    /// policies built on it.
    /// </summary>
    /// <remarks>
    /// The rule is: a status change ARMS an announce, a loop tick SENDS it. Sending from inside the
    /// status callback dropped the single-link link in 2026-08-04 and produced an FRMR storm on the
    /// relay in 2026-08-08 (DOC/captures/FRMR-ON-INNAK-2026-08-08). The fix lived on one runner path
    /// for four days before the other got it, because the two were copies.
    /// </remarks>
    public sealed class LinkAnnouncerTests
    {
        /// <summary>
        /// Reaching Active arms the announce but must NOT send it - this is the whole defect.
        /// </summary>
        [Fact]
        public void StatusActive_ArmsButDoesNotSend()
        {
            int sends = 0;
            LinkAnnouncer a = new LinkAnnouncer(() => LinkStatus.Active, () => sends++, enabled: true, onceOnly: false);

            a.OnStatusChanged(LinkStatus.Active);

            Assert.True(a.IsPending);
            Assert.Equal(0, sends);
        }

        /// <summary>
        /// The armed announce goes out on the next loop tick, by which time a whole batch of
        /// received frames has been processed.
        /// </summary>
        [Fact]
        public void LoopTick_SendsTheArmedAnnounceOnce()
        {
            int sends = 0;
            LinkAnnouncer a = new LinkAnnouncer(() => LinkStatus.Active, () => sends++, enabled: true, onceOnly: false);

            a.OnStatusChanged(LinkStatus.Active);

            Assert.True(a.OnLoopTick());
            Assert.Equal(1, sends);
            Assert.False(a.IsPending);

            Assert.False(a.OnLoopTick());   // nothing armed now
            Assert.Equal(1, sends);
        }

        /// <summary>
        /// A relay re-announces every time its link comes back. A once-only guard here is a bug: a
        /// restart bounces the link and the re-establish would get no announce, leaving the peer
        /// unable to reach us with the line looking perfectly healthy.
        /// </summary>
        [Fact]
        public void RelayPolicy_ReAnnouncesAfterTheLinkBounces()
        {
            int sends = 0;
            LinkStatus status = LinkStatus.Active;
            LinkAnnouncer a = new LinkAnnouncer(() => status, () => sends++, enabled: true, onceOnly: false);

            a.OnStatusChanged(LinkStatus.Active);
            a.OnLoopTick();

            status = LinkStatus.Starting;
            a.OnStatusChanged(LinkStatus.Starting);   // bounce
            status = LinkStatus.Active;
            a.OnStatusChanged(LinkStatus.Active);     // back up
            a.OnLoopTick();

            Assert.Equal(2, sends);
        }

        /// <summary>
        /// The single-link policy announces at most once, even across a bounce.
        /// </summary>
        [Fact]
        public void SingleLinkPolicy_AnnouncesAtMostOnce()
        {
            int sends = 0;
            LinkStatus status = LinkStatus.Active;
            LinkAnnouncer a = new LinkAnnouncer(() => status, () => sends++, enabled: true, onceOnly: true);

            a.OnStatusChanged(LinkStatus.Active);
            a.OnLoopTick();

            status = LinkStatus.Starting;
            a.OnStatusChanged(LinkStatus.Starting);
            status = LinkStatus.Active;
            a.OnStatusChanged(LinkStatus.Active);
            a.OnLoopTick();

            Assert.Equal(1, sends);
        }

        /// <summary>
        /// Disabled means never - the single-link path ships this way, because announcing there was
        /// measured to make D100 re-establish LAPB and lose the request.
        /// </summary>
        [Fact]
        public void Disabled_NeverSends()
        {
            int sends = 0;
            LinkAnnouncer a = new LinkAnnouncer(() => LinkStatus.Active, () => sends++, enabled: false, onceOnly: false);

            a.OnStatusChanged(LinkStatus.Active);

            Assert.False(a.IsPending);
            Assert.False(a.OnLoopTick());
            Assert.Equal(0, sends);
        }

        /// <summary>
        /// A link that dropped between being armed and the tick keeps its announce armed rather
        /// than firing it into a link that is re-establishing.
        /// </summary>
        [Fact]
        public void LinkDroppedBeforeTheTick_StaysArmedAndSendsWhenItReturns()
        {
            int sends = 0;
            LinkStatus status = LinkStatus.Active;
            LinkAnnouncer a = new LinkAnnouncer(() => status, () => sends++, enabled: true, onceOnly: false);

            a.OnStatusChanged(LinkStatus.Active);
            status = LinkStatus.Starting;

            Assert.False(a.OnLoopTick());
            Assert.Equal(0, sends);
            Assert.True(a.IsPending);

            status = LinkStatus.Active;
            Assert.True(a.OnLoopTick());
            Assert.Equal(1, sends);
        }
    }
}
