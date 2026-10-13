////GoalExtensions.cs//
// =============================================================================
// NEW FILE — drop into the OSWTools project at:  Extensions\GoalExtensions.cs
// The .csproj already compiles  Extensions\**\*.cs  so no project edit needed.
// Uses SafeKeyPart() from TwitchAdsExtensions.cs (same partial class).
// =============================================================================
using System;
using System.Globalization;

namespace OSWTools
{
    /// <summary>
    /// What AddGoalProgress() reports back after counting something.
    /// </summary>
    public class GoalProgressResult
    {
        /// <summary>Progress before this contribution.</summary>
        public long Before { get; set; }

        /// <summary>Progress after this contribution.</summary>
        public long After { get; set; }

        /// <summary>The target that was in effect when this contribution arrived.</summary>
        public long Target { get; set; }

        /// <summary>
        /// The target to use from now on. Same as Target unless the goal was
        /// reached AND raiseBy &gt; 0, in which case it has been raised (as
        /// many steps as needed) until it's above the new progress.
        /// </summary>
        public long NewTarget { get; set; }

        /// <summary>
        /// True only on the contribution that CROSSED the target. A goal
        /// that's already past its target (raiseBy 0) won't re-report.
        /// </summary>
        public bool Reached { get; set; }

        /// <summary>How many raiseBy steps were applied (0 if not reached).</summary>
        public int TimesRaised { get; set; }
    }

    /// <summary>
    /// Goal progress tracking — a reusable "count toward a target" engine.
    ///
    /// The DLL owns PROGRESS (a running number per goal) and the crossing /
    /// auto-raise math. The calling tool owns everything about what a goal
    /// MEANS (labels, units, which events count, where targets are saved),
    /// so this works for any goal a future widget can dream up.
    ///
    /// Progress is stored per tool, per goal id, in a PERSISTED global — a
    /// Streamer.bot restart mid-stream doesn't lose it. Resetting it (e.g.
    /// at stream start) is the caller's decision.
    ///
    ///   var r = lib.AddGoalProgress("twitch_bits", bits, target: 500, raiseBy: 10);
    ///   if (r.Reached) { /* celebrate, then save r.NewTarget as the target */ }
    /// </summary>
    public partial class OSWLib
    {
        // Streamer.bot can run several actions at the same moment (a gift
        // bomb is a burst of events). Without a lock, two of them could both
        // read "9", both write "10", and one contribution vanishes — a
        // classic "lost update". A static lock is shared by every OSWLib
        // instance in the Streamer.bot process. It's held only for a quick
        // read-add-write (microseconds), never while waiting on anything,
        // so it can't cause the kind of hangs long-running waits do.
        private static readonly object GoalLock = new object();

        private string GoalProgressKey(string goalId)
        {
            return "osw_Goal_" + SafeKeyPart(_ToolName) + "_" + SafeKeyPart(goalId);
        }

        /// <summary>Current progress for a goal (0 if never counted / reset).</summary>
        public long GetGoalProgress(string goalId)
        {
            // Stored as a string: numbers round-trip through Streamer.bot's
            // global storage most reliably as text (a long can come back
            // as an int or a double depending on how it was serialized).
            string raw = GetGlobal(GoalProgressKey(goalId), "0");
            long value;
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        /// <summary>
        /// Adds to a goal's progress and reports whether that crossed the target.
        ///
        /// target &lt;= 0 means "no goal set": progress is still counted (so
        /// turning a goal on mid-stream shows real numbers) but Reached is
        /// never reported.
        ///
        /// raiseBy &lt;= 0 means "don't auto-raise": the goal reports Reached
        /// once and then just keeps counting past its target.
        /// </summary>
        public GoalProgressResult AddGoalProgress(string goalId, long amount, long target, long raiseBy)
        {
            lock (GoalLock)
            {
                long before = GetGoalProgress(goalId);
                long after = amount > 0 ? before + amount : before;
                SetGlobal(GoalProgressKey(goalId), after.ToString(CultureInfo.InvariantCulture), true);

                var result = new GoalProgressResult
                {
                    Before = before,
                    After = after,
                    Target = target,
                    NewTarget = target
                };

                // Crossed = was below the line, now at or above it.
                if (target > 0 && before < target && after >= target)
                {
                    result.Reached = true;
                    if (raiseBy > 0)
                    {
                        // A loop, not a single +raiseBy: one 1,000-bit cheer
                        // on a 500 goal raising by 10 must land the next
                        // target ABOVE 1,000, or the goal would read as
                        // "already done" the moment it was raised.
                        long next = target;
                        int steps = 0;
                        while (next <= after)
                        {
                            next += raiseBy;
                            steps++;
                        }
                        result.NewTarget = next;
                        result.TimesRaised = steps;
                    }
                }
                return result;
            }
        }

        /// <summary>Sets a goal's progress back to 0.</summary>
        public void ResetGoalProgress(string goalId)
        {
            lock (GoalLock)
            {
                SetGlobal(GoalProgressKey(goalId), "0", true);
            }
        }
    }
}
