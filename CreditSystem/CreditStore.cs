// =============================================================================
// OSWTools — CreditSystem/CreditStore.cs
//
// NEW FILE. Loads and saves the Credit System's three JSON files through the
// DLL's EXISTING storage layer (Data/OSWData.cs → SafeWriter atomic writes
// with .bak backups). Nothing here talks to Streamer.bot.
//
//   OSWData\Credits\ledger.json    — credits, redemptions, receipts, journal
//   OSWData\Credits\config.json    — streamer settings
//   OSWData\Credits\triggers.json  — things credit can be spent on
//
// ─────────────────────────────────────────────────────────────────────────────
// THE BIG IDEA: "read-modify-write under one lock"
//
//   Streamer.bot runs actions IN PARALLEL. Picture two cheers failing at the
//   same moment:
//
//     Action A: load ledger (3 credits) ─────────────── add 1 → save (4)
//     Action B:      load ledger (3 credits) ── add 1 → save (4)   ← A's credit LOST
//
//   This is called a "race condition", and with money it's unacceptable.
//
//   The fix: every change goes through Mutate(), which holds ONE lock for
//   the whole load → change → save cycle. Action B waits until A is fully
//   done, then sees A's credit. Actions just queue for a few milliseconds.
//
//   SafeWriter already locks each file WRITE — but that alone doesn't stop
//   the race above, because the race is between the READ and the WRITE.
//   That's why this class adds its own lock around the whole operation.
//
// ─────────────────────────────────────────────────────────────────────────────
// CACHING
//   The ledger is loaded from disk once, then kept in memory (_ledger). Every
//   Mutate() saves to disk before returning, so the file is always current
//   — the cache just skips re-reading it on every call.
//
//   Because Streamer.bot loads OSWTools.dll once per session, "static" here
//   means "shared by every action, for as long as Streamer.bot is open".
// =============================================================================

using System;
using System.Collections.Generic;
using OSWTools.Data;

namespace OSWTools
{
    internal static class OSWCreditStore
    {
        // ── File locations (passed to OSWData) ──────────────────────────────
        internal const string DataTool     = "Credits";
        private  const string LedgerFile   = "ledger";
        private  const string ConfigFile   = "config";
        private  const string TriggersFile = "triggers";

        // ── The one lock ────────────────────────────────────────────────────
        // A plain object used only as a lock "key". Every read or write of
        // the cached data below happens inside lock (_gate) { ... }.
        private static readonly object _gate = new object();

        // ── In-memory caches (null = not loaded yet) ────────────────────────
        private static OSWCreditLedger _ledger;
        private static OSWCreditConfig _config;
        private static Dictionary<string, OSWCreditTrigger> _triggers;

        // ═════════════════════════════════════════════════════════════════════
        //  LEDGER
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Runs <paramref name="change"/> against the live ledger while holding
        /// the lock, saves the ledger to disk, then returns whatever the change
        /// returned. ALL ledger modifications must go through here.
        ///
        /// LEARNING NOTE — Func&lt;OSWCreditLedger, T&gt;
        ///   A Func is "a function passed around like a value". The caller
        ///   hands us the WHAT (their change, usually a lambda: ledger => ...),
        ///   and this method handles the HOW (lock, load, save). This pattern
        ///   is often called a "unit of work".
        /// </summary>
        internal static T Mutate<T>(Func<OSWCreditLedger, T> change)
        {
            lock (_gate)
            {
                OSWCreditLedger ledger = GetLedgerUnsafe();
                T result = change(ledger);
                OSWData.Save(DataTool, LedgerFile, ledger);
                return result;
            }
        }

        /// <summary>
        /// Read-only access to the ledger under the lock. Nothing is saved.
        /// IMPORTANT: copy what you need out inside <paramref name="read"/> —
        /// don't hand live list references back to callers, or they could be
        /// changed by another action while you're still looking at them.
        /// </summary>
        internal static T Read<T>(Func<OSWCreditLedger, T> read)
        {
            lock (_gate)
            {
                return read(GetLedgerUnsafe());
            }
        }

        /// <summary>
        /// Loads the ledger if it isn't cached. "Unsafe" in the name is a
        /// reminder: only call this while ALREADY holding _gate.
        /// </summary>
        private static OSWCreditLedger GetLedgerUnsafe()
        {
            if (_ledger != null) return _ledger;

            try
            {
                _ledger = OSWData.LoadOrDefault(DataTool, LedgerFile, new OSWCreditLedger());
            }
            catch (Exception)
            {
                // The main file is corrupt (e.g. PC lost power mid-write on a
                // drive that doesn't honor atomic moves). SafeWriter keeps a
                // .bak of the previous good version — try that before giving
                // up. If the backup also fails we start empty, but we RENAME
                // nothing and delete nothing, so the files are still there for
                // manual recovery.
                if (OSWData.RestoreBackup(DataTool, LedgerFile))
                {
                    try { _ledger = OSWData.Load<OSWCreditLedger>(DataTool, LedgerFile); }
                    catch (Exception) { _ledger = null; }
                }
                if (_ledger == null)
                {
                    _ledger = new OSWCreditLedger();
                    LastLoadWarning = "ledger.json and its backup could not be read — started with an empty ledger. " +
                                      "Old files were left in place for manual recovery.";
                    // Don't save over the broken file automatically — the
                    // first real Mutate() will, by which point the warning
                    // has been logged by OSWLib.
                }
            }

            // Defensive: JSON with "Credits": null would leave the list null.
            if (_ledger.Credits     == null) _ledger.Credits     = new List<OSWCredit>();
            if (_ledger.Redemptions == null) _ledger.Redemptions = new List<OSWCreditRedemption>();
            if (_ledger.Receipts    == null) _ledger.Receipts    = new List<OSWCreditReceipt>();
            if (_ledger.Journal     == null) _ledger.Journal     = new List<OSWCreditJournalEntry>();

            return _ledger;
        }

        /// <summary>
        /// Set when the ledger had to be started empty because the file was
        /// unreadable. OSWLib checks this and logs it once (this static class
        /// has no access to CPH, so it can't log by itself).
        /// </summary>
        internal static string LastLoadWarning { get; set; }

        /// <summary>
        /// Drops the in-memory copy so the next call re-reads from disk.
        /// Useful if the streamer hand-edits ledger.json while SB is running.
        /// </summary>
        internal static void ReloadFromDisk()
        {
            lock (_gate)
            {
                _ledger   = null;
                _config   = null;
                _triggers = null;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  CONFIG
        // ═════════════════════════════════════════════════════════════════════

        internal static OSWCreditConfig GetConfig()
        {
            lock (_gate)
            {
                if (_config != null) return _config;

                bool firstRun = !OSWData.Exists(DataTool, ConfigFile);
                _config = OSWData.LoadOrDefault(DataTool, ConfigFile, new OSWCreditConfig());

                if (firstRun)
                {
                    // First time ever: default the base currency to this PC's
                    // region (reusing the existing OSWCurrencies helper), and
                    // write the file so the streamer can see/edit every setting.
                    _config.BaseCurrency = OSWCurrencies.LocalCode();
                    OSWData.Save(DataTool, ConfigFile, _config);
                }
                return _config;
            }
        }

        internal static void SaveConfig(OSWCreditConfig config)
        {
            if (config == null) return;
            lock (_gate)
            {
                _config = config;
                OSWData.Save(DataTool, ConfigFile, config);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  TRIGGERS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>Snapshot copy of every registered trigger.</summary>
        internal static List<OSWCreditTrigger> GetTriggers()
        {
            lock (_gate)
            {
                return new List<OSWCreditTrigger>(GetTriggersUnsafe().Values);
            }
        }

        /// <summary>One trigger by key (case-insensitive), or null.</summary>
        internal static OSWCreditTrigger GetTrigger(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            lock (_gate)
            {
                OSWCreditTrigger t;
                GetTriggersUnsafe().TryGetValue(key.Trim(), out t);
                return t;
            }
        }

        /// <summary>Adds or replaces a trigger and saves.</summary>
        internal static void UpsertTrigger(OSWCreditTrigger trigger)
        {
            lock (_gate)
            {
                Dictionary<string, OSWCreditTrigger> all = GetTriggersUnsafe();
                all[trigger.Key] = trigger;
                OSWData.Save(DataTool, TriggersFile, new List<OSWCreditTrigger>(all.Values));
            }
        }

        internal static bool RemoveTrigger(string key)
        {
            lock (_gate)
            {
                Dictionary<string, OSWCreditTrigger> all = GetTriggersUnsafe();
                if (!all.Remove(key)) return false;
                OSWData.Save(DataTool, TriggersFile, new List<OSWCreditTrigger>(all.Values));
                return true;
            }
        }

        private static Dictionary<string, OSWCreditTrigger> GetTriggersUnsafe()
        {
            if (_triggers != null) return _triggers;

            // Saved as a list (nicer to hand-edit), kept in memory as a
            // dictionary (fast lookup by key). OrdinalIgnoreCase makes "TTS"
            // and "tts" the same key.
            List<OSWCreditTrigger> list = OSWData.LoadOrDefault(
                DataTool, TriggersFile, new List<OSWCreditTrigger>());

            _triggers = new Dictionary<string, OSWCreditTrigger>(StringComparer.OrdinalIgnoreCase);
            foreach (OSWCreditTrigger t in list)
            {
                if (t != null && !string.IsNullOrWhiteSpace(t.Key))
                    _triggers[t.Key] = t;
            }
            return _triggers;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SMALL HELPERS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Makes a short readable ID like "CR-7F3A9C". 6 hex characters = 16.7
        /// million combinations, and we re-roll on the (rare) collision, so
        /// IDs are unique within a ledger while still easy to type in chat.
        /// </summary>
        internal static string NewId(string prefix, Func<string, bool> alreadyUsed)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                string id = prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
                if (alreadyUsed == null || !alreadyUsed(id)) return id;
            }
            // Practically unreachable — fall back to a full GUID.
            return prefix + "-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        }

        /// <summary>Adds a journal entry and trims the oldest beyond the cap.</summary>
        internal static void Journal(OSWCreditLedger ledger, OSWCreditJournalEntry entry, int maxEntries)
        {
            ledger.Journal.Add(entry);
            if (maxEntries > 0 && ledger.Journal.Count > maxEntries)
                ledger.Journal.RemoveRange(0, ledger.Journal.Count - maxEntries);
        }
    }
}
