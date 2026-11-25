// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Functions/GifBattler/GifBattlerState.cs      DLL v1.2.0
//
//  Every session global the GIF Battler reads or writes, in one place.
//
//  WHY THIS FILE EXISTS:
//    The old scripts typed "gif_autoQueue_user" etc. by hand in nine
//    different actions, and each had its own ClearBattleState /
//    ClearChallengeState / ClearAutoQueueState copy. One typo in one
//    copy = a state that never clears. Now the key names are constants
//    and each "clear" exists exactly once.
//
//  WHY "internal":
//    Nothing outside the DLL needs to touch these keys directly — the
//    Streamer.bot script only talks to GifBattlerEngine. Marking the
//    class internal keeps it out of the public API (it won't show up
//    in autocomplete in SB) and lets an obfuscator rename it freely.
//
//  TIME VALUES — ★ CHANGED:
//    Old code stored DateTime.Now.ToString("o") and read it back with
//    DateTime.TryParse. That round-trip is what caused the earlier
//    cooldown/timezone bug. Times are now stored as UTC *ticks* (a
//    plain long number, saved as text). A number can't be "helpfully"
//    converted between timezones on the way back in, so the bug class
//    is gone entirely.
//
//  The global KEY NAMES are unchanged, so nothing else (overlay,
//  other tools reading these globals) breaks.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Globalization;

namespace OSWTools.Functions.GifBattler
{
    internal sealed class GifBattlerState
    {
        // ── Key names (unchanged from the old scripts) ────────────────
        internal const string K_BattleActive   = "gif_battleActive";
        internal const string K_BattleUserA    = "gif_battle_userA";
        internal const string K_BattleUserB    = "gif_battle_userB";
        internal const string K_BattleChoiceA  = "gif_battle_choiceA";   // ★ NEW — exact poll choice text
        internal const string K_BattleChoiceB  = "gif_battle_choiceB";   // ★ NEW
        internal const string K_PollTitle      = "gif_pollTitle";
        internal const string K_PollId         = "gif_activePollId";

        internal const string K_QueueUrl       = "gif_autoQueue_url";
        internal const string K_QueueUser      = "gif_autoQueue_user";
        internal const string K_QueueUserId    = "gif_autoQueue_userId";
        internal const string K_QueueAvatar    = "gif_autoQueue_avatar";
        internal const string K_QueueExpiry    = "gif_autoQueue_expiry";

        internal const string K_ChalUrl        = "gif_challenge_url";
        internal const string K_ChalUser       = "gif_challenge_user";
        internal const string K_ChalUserId     = "gif_challenge_userId";
        internal const string K_ChalAvatar     = "gif_challenge_avatar";
        internal const string K_ChalTarget     = "gif_challenge_targetUser";
        internal const string K_ChalExpiry     = "gif_challenge_expiry";

        internal const string K_GlobalCooldown = "gif_globalCd";          // ★ NEW
        internal const string K_UserCdPrefix   = "gif_userCd_";
        internal const string K_NativeSeen     = "gif_nativeSeenIds";     // ★ NEW (step 2b)

        private const int NATIVE_SEEN_MAX = 30;

        private readonly OSWLib _lib;

        internal GifBattlerState(OSWLib lib)
        {
            _lib = lib;
        }

        // ── Small typed helpers (all session-only, never persisted) ───
        private string Str(string key)           => _lib.GetGlobalSession(key, "") ?? "";
        private void   Set(string key, string v) => _lib.SetGlobal(key, v ?? "", persisted: false);

        // ═════════════════════════════════════════════════════════════
        //  BATTLE
        // ═════════════════════════════════════════════════════════════
        internal bool   BattleActive  => _lib.GetGlobalSession(K_BattleActive, false);
        internal string BattleUserA   => Str(K_BattleUserA);
        internal string BattleUserB   => Str(K_BattleUserB);
        internal string BattleChoiceA => Str(K_BattleChoiceA);
        internal string BattleChoiceB => Str(K_BattleChoiceB);
        internal string PollId        => Str(K_PollId);

        internal void StartBattle(string userA, string userB, string pollTitle, string choiceA, string choiceB)
        {
            _lib.SetGlobal(K_BattleActive, true, persisted: false);
            Set(K_BattleUserA,   userA);
            Set(K_BattleUserB,   userB);
            Set(K_PollTitle,     pollTitle);
            Set(K_BattleChoiceA, choiceA);
            Set(K_BattleChoiceB, choiceB);
        }

        internal void SetPollId(string pollId) => Set(K_PollId, pollId);

        internal void ClearBattle()
        {
            _lib.SetGlobal(K_BattleActive, false, persisted: false);
            Set(K_BattleUserA,   "");
            Set(K_BattleUserB,   "");
            Set(K_BattleChoiceA, "");
            Set(K_BattleChoiceB, "");
            Set(K_PollTitle,     "");
            Set(K_PollId,        "");
        }

        // ═════════════════════════════════════════════════════════════
        //  AUTO-BATTLE QUEUE  (Challenger A waiting for a Challenger B)
        // ═════════════════════════════════════════════════════════════
        internal string QueueUser   => Str(K_QueueUser);
        internal string QueueUrl    => Str(K_QueueUrl);
        internal string QueueAvatar => Str(K_QueueAvatar);

        /// <summary>
        /// ★ NEW SAFETY CHECK. True only if someone is queued AND their
        /// window hasn't run out. Before, the engine trusted the timer
        /// alone — if the timer misfired, a stale queue could pair two
        /// viewers minutes apart (or reset too early). Now the stored
        /// expiry is the source of truth and the timer is just cleanup.
        /// </summary>
        internal bool QueueIsOpen
        {
            get
            {
                if (string.IsNullOrEmpty(QueueUser)) return false;
                long expiry = ReadTicks(K_QueueExpiry);
                return expiry == 0 || DateTime.UtcNow.Ticks < expiry;
            }
        }

        internal void OpenQueue(string url, string user, string userId, string avatar, int windowSeconds)
        {
            Set(K_QueueUrl,    url);
            Set(K_QueueUser,   user);
            Set(K_QueueUserId, userId);
            Set(K_QueueAvatar, avatar);
            WriteTicks(K_QueueExpiry, DateTime.UtcNow.AddSeconds(windowSeconds).Ticks);
        }

        internal void ClearQueue()
        {
            Set(K_QueueUrl,    "");
            Set(K_QueueUser,   "");
            Set(K_QueueUserId, "");
            Set(K_QueueAvatar, "");
            Set(K_QueueExpiry, "");
        }

        // ═════════════════════════════════════════════════════════════
        //  CHALLENGE  (A named a specific opponent)
        // ═════════════════════════════════════════════════════════════
        internal string ChallengeTarget => Str(K_ChalTarget);
        internal string ChallengeUser   => Str(K_ChalUser);
        internal string ChallengeUrl    => Str(K_ChalUrl);
        internal string ChallengeAvatar => Str(K_ChalAvatar);

        internal void OpenChallenge(string url, string user, string userId, string avatar, string target, int windowSeconds)
        {
            Set(K_ChalUrl,    url);
            Set(K_ChalUser,   user);
            Set(K_ChalUserId, userId);
            Set(K_ChalAvatar, avatar);
            Set(K_ChalTarget, target);
            WriteTicks(K_ChalExpiry, DateTime.UtcNow.AddSeconds(windowSeconds).Ticks);
        }

        internal void ClearChallenge()
        {
            Set(K_ChalUrl,    "");
            Set(K_ChalUser,   "");
            Set(K_ChalUserId, "");
            Set(K_ChalAvatar, "");
            Set(K_ChalTarget, "");
            Set(K_ChalExpiry, "");
        }

        // ═════════════════════════════════════════════════════════════
        //  COOLDOWNS
        // ═════════════════════════════════════════════════════════════

        /// <summary>Seconds left on this viewer's cooldown, or 0 if free.</summary>
        internal int UserCooldownRemaining(string userId) => SecondsLeft(K_UserCdPrefix + userId);

        internal void StartUserCooldown(string userId, int seconds)
        {
            if (seconds <= 0 || string.IsNullOrEmpty(userId)) return;
            WriteTicks(K_UserCdPrefix + userId, DateTime.UtcNow.AddSeconds(seconds).Ticks);
        }

        internal int GlobalCooldownRemaining() => SecondsLeft(K_GlobalCooldown);

        internal void StartGlobalCooldown(int seconds)
        {
            if (seconds <= 0) return;
            WriteTicks(K_GlobalCooldown, DateTime.UtcNow.AddSeconds(seconds).Ticks);
        }

        // ═════════════════════════════════════════════════════════════
        //  NATIVE GIF DE-DUPLICATION  ★ step 2b
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Records a Twitch message ID. Returns true the FIRST time an ID is
        /// seen, false on any repeat.
        ///
        /// WHY: one native GIF can reach the engine more than once —
        ///   • the Chat Message trigger (Router A/B) AND the overlay bridge,
        ///   • or two copies of the overlay open (OBS + a browser preview),
        /// each of which sends its own DoAction. Twitch's message ID is the
        /// same in every copy, so remembering the last 30 IDs stops doubles.
        ///
        /// Stored as one "id|id|id" string — a tiny rolling list that lives
        /// in a session global, so it resets with Streamer.bot.
        /// Empty ID = can't de-dupe → allowed through.
        /// </summary>
        internal bool MarkNativeMessageSeen(string messageId)
        {
            if (string.IsNullOrEmpty(messageId)) return true;

            var ids = new System.Collections.Generic.List<string>(
                Str(K_NativeSeen).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));

            if (ids.Contains(messageId)) return false;

            ids.Add(messageId);
            while (ids.Count > NATIVE_SEEN_MAX) ids.RemoveAt(0);
            Set(K_NativeSeen, string.Join("|", ids));
            return true;
        }

        // ── Tick storage ──────────────────────────────────────────────
        //  Stored as text so SB's global viewer shows something readable
        //  and so an old "o"-format value left over from the previous
        //  version simply fails to parse (→ treated as "no cooldown")
        //  instead of throwing.
        private int SecondsLeft(string key)
        {
            long expiry = ReadTicks(key);
            if (expiry == 0) return 0;
            long remaining = expiry - DateTime.UtcNow.Ticks;
            if (remaining <= 0) return 0;
            return (int)Math.Ceiling(TimeSpan.FromTicks(remaining).TotalSeconds);
        }

        private long ReadTicks(string key)
        {
            long ticks;
            return long.TryParse(Str(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ? ticks : 0;
        }

        private void WriteTicks(string key, long ticks)
        {
            Set(key, ticks.ToString(CultureInfo.InvariantCulture));
        }
    }
}
