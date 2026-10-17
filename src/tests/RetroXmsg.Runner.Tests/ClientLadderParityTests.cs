//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroXmsg - Accessing yesterday's Xmsg API with today's standards
//

using System;
using System.Reflection;
using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Live;
using RetroIO.Protocols.Xmsg.Servers.Fa;
using RetroXmsg.Runner;
using Xunit;
namespace RetroXmsg.Runner.Tests
{
    /// <summary>
    /// The push and the pull are two implementations of one ladder, and a fix to either must reach
    /// the other.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect this guards, and it has now happened THREE times</b></para>
    /// <para>
    /// A fault is found on one side, fixed and proved there, and the other side is left with it.
    /// The repaired side then works, which makes the broken side look like a fault of the MACHINE
    /// or of that DIRECTION rather than a hole in our own code.
    /// </para>
    /// <para>
    /// 2026-08-17: the seed gate reached the pull and not the push. 2026-08-18: the reverse.
    /// <c>SyncReadinessWiringTests</c> exists because of those two.
    /// </para>
    /// <para>
    /// 2026-08-21, the third: the CONNECT-LETTER RETRY. <c>FaPushRun</c> grew it on 2026-08-17
    /// because a letter sent before the peer was ready is ignored and never sent again -
    /// "a dead transfer with a healthy-looking log", in its own words. <c>FaPullRun</c> never got
    /// it. For four days a pull whose first letter went unanswered sat silent for the whole
    /// 240-second transfer timeout and then reported only "did NOT finish within 240s", which names
    /// nothing. That single uninformative line sent a day into suspecting XMSG, the PLANC compiler,
    /// the restart script, the chat programs and the machine.
    /// </para>
    /// <para>
    /// It also could not simply be ported, because the retry has to TELL THE DRIVER it has given up
    /// and <see cref="FaReadDriver"/> had no <c>Abandon</c> - a method
    /// <see cref="FaWriteDriver"/> had held since 2026-08-18. A missing capability on one side
    /// silently blocks the fix on the other, which is the same disease one level down.
    /// </para>
    /// <para><b>What is asserted</b></para>
    /// <para>
    /// The PROPERTIES whose absence caused each episode, not the behaviour of a live transfer -
    /// exercising the retry itself needs a peer that goes quiet at the right moment, and that is a
    /// live-run check. These are the wiring checks, and wiring is exactly what kept drifting.
    /// </para>
    /// </remarks>
    public class ClientLadderParityTests
    {
        /// <summary>
        /// Both FA drivers must be able to be told the caller gave up.
        /// </summary>
        /// <remarks>
        /// Without this the driver reports itself unfinished after the caller has already decided
        /// the transfer is dead. MEASURED 2026-08-18 on the push: a transfer to a user that does not
        /// exist printed "GIVING UP" at 25 seconds and the process then sat until the wall-clock
        /// timeout at 45 - it had known the answer for twenty seconds and had no way to say it.
        /// </remarks>
        [Fact]
        public void BothFaDriversCanBeAbandoned()
        {
            MethodInfo? write = typeof(FaWriteDriver).GetMethod(
                "Abandon", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo? read = typeof(FaReadDriver).GetMethod(
                "Abandon", BindingFlags.Public | BindingFlags.Instance);

            Assert.True(
                write != null,
                "FaWriteDriver.Abandon has existed since 2026-08-18 - if it has gone, the push can"
                    + " no longer tell its driver it gave up and a decided failure will be reported"
                    + " as a transfer still in progress.");

            Assert.True(
                read != null,
                "FaReadDriver.Abandon is missing. The PULL then has no way to say it gave up, which"
                    + " is precisely why the connect-letter retry could not be ported to it for four"
                    + " days. A capability present on one driver and not the other silently blocks"
                    + " the fix on the side that lacks it.");
        }

        /// <summary>
        /// Both one-shot runs must keep the connect letter so it can be sent again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The letter goes out as soon as the link exists, and "the link exists" becomes true on ANY
        /// inbound frame - including the peer telling us it has restarted. So it routinely goes out
        /// before the peer will answer it. Sent once and never repeated, that is a four-minute hang
        /// with nothing in the log to say why.
        /// </para>
        /// <para>
        /// The field is asserted rather than the retry behaviour because the bytes must be KEPT and
        /// re-sent, never rebuilt: a retransmit has to carry the SAME sequence, and rebuilding would
        /// advance the ladder instead of repeating the step. A run that rebuilt instead would still
        /// "retry" and would still fail.
        /// </para>
        /// </remarks>
        [Fact]
        public void BothOneShotRunsKeepTheConnectLetterForRetry()
        {
            FieldInfo? push = typeof(FaPushRun).GetField(
                "_connectFrames", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? pull = typeof(FaPullRun).GetField(
                "_connectFrames", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.True(
                push != null,
                "FaPushRun must keep the connect letter. It has done since 2026-08-17, and that was"
                    + " the single thing every client-side failure of that day had in common.");

            Assert.True(
                pull != null,
                "FaPullRun must keep the connect letter so it can be sent again. Without it a pull"
                    + " whose first letter is unanswered waits out the entire transfer timeout and"
                    + " then blames nothing in particular.");
        }

        /// <summary>
        /// Both one-shot runs must count their attempts, so giving up is a decision and not a hang.
        /// </summary>
        /// <remarks>
        /// Retrying for ever is not better than not retrying: a flood cannot be diagnosed either.
        /// The count is what turns silence into a bounded, explained failure.
        /// </remarks>
        [Fact]
        public void BothOneShotRunsCountConnectAttempts()
        {
            FieldInfo? push = typeof(FaPushRun).GetField(
                "_connectAttempts", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? pull = typeof(FaPullRun).GetField(
                "_connectAttempts", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.True(push != null, "FaPushRun must bound its connect-letter attempts.");
            Assert.True(
                pull != null,
                "FaPullRun must bound its connect-letter attempts and give up out loud. A stall that"
                    + " says why is diagnosable; one that says 'did NOT finish within 240s' is not,"
                    + " and that message cost a full day of looking in the wrong places.");
        }

        /// <summary>
        /// Both one-shot runs must tell an answer to THIS conversation from other link traffic.
        /// </summary>
        /// <remarks>
        /// The peer's own requests to our file server arrive on the same link moments later. A flag
        /// set on any inbound frame marks the letter answered when it was not, which silently
        /// disables the retry - the exact failure the retry exists to cure, reintroduced one layer
        /// up. The push hit this while being fixed; the port carries the same guard.
        /// </remarks>
        [Fact]
        public void BothOneShotRunsTrackWhetherThePeerAnsweredUs()
        {
            FieldInfo? push = typeof(FaPushRun).GetField(
                "_peerAnswered", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? pull = typeof(FaPullRun).GetField(
                "_peerAnswered", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.True(push != null, "FaPushRun must know whether the peer answered US.");
            Assert.True(
                pull != null,
                "FaPullRun must know whether the peer answered US rather than merely whether the"
                    + " link is busy, or the retry it now has will be disabled by unrelated traffic.");
        }
    }
}
