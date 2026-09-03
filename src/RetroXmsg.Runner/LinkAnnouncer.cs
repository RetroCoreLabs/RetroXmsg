using System;

using RetroIO.Protocols.Xmsg.Node.Seam;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Live;
namespace RetroXmsg.Runner
{
    /// <summary>
    /// Decides WHEN a reachability announce is sent after a link comes up: armed by the link's
    /// status change, sent on the next loop tick.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a type and not two lines in each runner</b></para>
    /// <para>
    /// Sending an announce from inside the status callback is a real, measured defect and it has
    /// bitten this project twice.
    /// </para>
    /// <para>
    /// On the single-link path (2026-08-04) sending from the callback re-entered the LAPB adapter
    /// while it was still completing the transition, and the link dropped straight back to
    /// establishing 260 ms later.
    /// </para>
    /// <para>
    /// On the relay path (2026-08-08) the same shape produced a worse failure. The callback fires
    /// part-way through a BATCH of received frames, and a peer's queued SABM retries arrive six in
    /// one TCP segment. The announce went out after the first SABM, the remaining five each
    /// hard-zeroed V(S) as spec 3.2 requires, and the peer's correct acknowledgement then fell
    /// outside the window - so we answered FRMR and the link never carried traffic. See
    /// <c>DOC/captures/FRMR-ON-INNAK-2026-08-08</c>.
    /// </para>
    /// <para>
    /// The fix existed on the single-link path for four days before the relay path got it, because
    /// the two were copies. Putting the mechanism in one place is what stops that happening a third
    /// time.
    /// </para>
    /// <para><b>The two paths disagree on POLICY, and both are deliberate</b></para>
    /// <para>
    /// This type carries the mechanism only. Whether to announce at all, and whether to repeat it,
    /// stay per-path decisions because they were each measured:
    /// </para>
    ///  - The RELAY must announce on EVERY link-up. A peer does not register us just because the
    ///    link is Active; it reports "Remote system is not accessible" with the line perfectly
    ///    healthy. A once-only guard is a bug there, because a restart bounces both links and the
    ///    re-establish would get no announce.
    ///  - The SINGLE-LINK path has it OFF. Measured 2026-08-04: announcing made D100 re-establish
    ///    LAPB and the request was lost in the reset. Whether D100 is right to do that is UNKNOWN.
    /// </remarks>
    internal sealed class LinkAnnouncer
    {
        private readonly Func<LinkStatus> _readStatus;
        private readonly Action _send;
        private readonly bool _enabled;
        private readonly bool _onceOnly;

        private bool _pending;
        private bool _sentOnce;

        /// <summary>
        /// Initialises the announcer for one link.
        /// </summary>
        /// <param name="readStatus">
        /// Reads the link's CURRENT status, checked again at send time - the link can drop between
        /// being armed and the next tick.
        /// </param>
        /// <param name="send">
        /// Sends the announce. Invoked only from <see cref="OnLoopTick"/>, never from
        /// <see cref="OnStatusChanged"/>.
        /// </param>
        /// <param name="enabled">
        /// False disables announcing entirely for this link.
        /// </param>
        /// <param name="onceOnly">
        /// True announces only the first time the link becomes Active; false re-announces on every
        /// Active, which is what a relay needs.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="readStatus"/> or <paramref name="send"/> is null.
        /// </exception>
        public LinkAnnouncer(Func<LinkStatus> readStatus, Action send, bool enabled, bool onceOnly)
        {
            _readStatus = readStatus ?? throw new ArgumentNullException(nameof(readStatus));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _enabled = enabled;
            _onceOnly = onceOnly;
        }

        /// <summary>
        /// Gets a value indicating whether an announce is armed and waiting for the next tick.
        /// </summary>
        public bool IsPending
        {
            get { return _pending; }
        }

        /// <summary>
        /// Arms the announce when the link reaches Active. Deliberately does NOT send.
        /// </summary>
        /// <param name="newStatus">
        /// The status the link has just entered.
        /// </param>
        public void OnStatusChanged(LinkStatus newStatus)
        {
            if (!_enabled || newStatus != LinkStatus.Active)
            {
                return;
            }

            if (_onceOnly && _sentOnce)
            {
                return;
            }

            _pending = true;
        }

        /// <summary>
        /// Sends the armed announce, if the link is still Active.
        /// </summary>
        /// <returns>
        /// True when an announce was sent.
        /// </returns>
        /// <remarks>
        /// By the time a loop tick runs, every frame from the last batch has been processed - a
        /// whole SABM burst included - so the sequence state is settled and the announce cannot
        /// land between two resets. The status is re-read here because the link may have dropped
        /// since it was armed.
        /// </remarks>
        public bool OnLoopTick()
        {
            if (!_pending)
            {
                return false;
            }

            if (_readStatus() != LinkStatus.Active)
            {
                return false;   // stay armed; try again once it is up
            }

            _pending = false;
            _sentOnce = true;
            _send();
            return true;
        }
    }
}
