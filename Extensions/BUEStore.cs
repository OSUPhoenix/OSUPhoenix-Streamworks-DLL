// =============================================================================
// OSWTools — Extensions/BUEStore.cs                                  (NEW)
//
// "Blow Up Emote" (module code: BUE) — where settings and progress are SAVED.
//
// WHY THIS FILE EXISTS
//   Version 1 kept the config, the balloon state and the picture cache in
//   Streamer.bot GLOBAL VARIABLES. Globals are meant for small values (a
//   number, a flag), not whole documents: they are awkward to edit, can't be
//   backed up as a file, and every chat message re-parsed the whole JSON
//   string. Your other tools already solved this: OSWData saves JSON files to
//       {Streamer.bot folder}/OSWData/{tool}/{file}.json
//   with safe atomic writes and a .bak backup. BUE now uses the same system:
//
//       OSWData/BlowUpEmote/config_default.json   your settings
//       OSWData/BlowUpEmote/state_default.json    fill %, leaderboards, pops
//       OSWData/BlowUpEmote/images_default.json   emote name -> picture URL
//
// HOW IT WORKS (the approach)
//   1. IN-MEMORY CACHE. Reading a file on every chat message would be wasteful,
//      so each file is loaded once and kept in memory.
//   2. TIMESTAMP CHECK. Before handing out the cached copy we ask the file
//      system "when was this file last changed?" (a very cheap call). If it
//      changed since we loaded it - because YOU hand-edited it - we reload.
//      So editing config_default.json while Streamer.bot runs just works.
//   3. WRITE-THROUGH. When code saves, it writes the file immediately and
//      remembers the new timestamp, so our own save doesn't trigger a reload.
//   4. ONE LOCK. Streamer.bot can run the same action for two chat messages at
//      nearly the same moment. Anything that does "load -> change -> save"
//      should wrap those steps in  lock (BueStore.SyncRoot) { ... }  so two
//      messages can't both start from the same old fill level and overwrite
//      each other. (BUEAction.cs does exactly this.)
//
// TWO WAYS TO GET THE CONFIG - pick the right one:
//   LoadConfig(id)      the SHARED live object. Read it, never change it.
//                       (Chat handling uses this: fast, no copying.)
//   LoadConfigCopy(id)  your own private COPY. Change it freely, then pass it
//                       to SaveConfig(). (Settings window and Set Emote use this.)
//
// Streamer.bot globals are still used for one thing only: the old values are
// read ONCE by OSWLib.BueMigrateLegacy() to import them into these files.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using OSWTools.Data;

namespace OSWTools.BlowUpEmote
{
    public static class BueStore
    {
        /// <summary>Folder name under OSWData/.</summary>
        public const string Tool = "BlowUpEmote";

        /// <summary>Build tag of the DLL code. Logged by Start/Cancel/Status so you can confirm the rebuilt DLL is the one Streamer.bot is running.</summary>
        public const string BuildTag = "2.3.0";

        /// <summary>Lock for any load -> change -> save sequence. See header.</summary>
        public static readonly object SyncRoot = new object();

        /// <summary>
        /// Where warnings go. Set by OSWLib.BueMigrateLegacy() so problems land in
        /// the Streamer.bot log. Null = stay quiet (never throws if unset).
        /// </summary>
        public static Action<string> Logger;

        private class Entry
        {
            public object Value;
            public DateTime StampUtc;
        }

        private static readonly Dictionary<string, Entry> _cache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        // ── File names ───────────────────────────────────────────────────

        // Only letters, digits, - and _ survive, so a TargetId can't build a
        // strange path.
        private static string SafeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "default";
            var sb = new StringBuilder();
            foreach (char c in id.Trim())
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        public static string ConfigFile(string targetId) { return "config_" + SafeId(targetId); }
        public static string StateFile(string targetId) { return "state_" + SafeId(targetId); }
        public static string ImagesFile(string targetId) { return "images_" + SafeId(targetId); }

        /// <summary>Full path of the config file (for showing in the settings window).</summary>
        public static string ConfigPath(string targetId)
        {
            return OSWData.GetFilePath(Tool, ConfigFile(targetId));
        }

        public static bool ConfigExists(string targetId)
        {
            return OSWData.Exists(Tool, ConfigFile(targetId));
        }

        private static void Warn(string text)
        {
            try { if (Logger != null) Logger("[BUE] " + text); } catch { /* logging must never break the tool */ }
        }

        // ── Generic cached load / save ───────────────────────────────────

        private static DateTime StampOf(string file)
        {
            string path = OSWData.GetFilePath(Tool, file);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }

        // makeDefault: what to use when there is no file yet (or it is unreadable).
        // fix:         tidies a freshly loaded object (clamp numbers, fill nulls...)
        //              and may return a different instance.
        private static T LoadCached<T>(string file, Func<T> makeDefault, Func<T, T> fix, bool alwaysReload = false) where T : class
        {
            lock (SyncRoot)
            {
                DateTime stamp = StampOf(file);

                Entry e;
                if (!alwaysReload && _cache.TryGetValue(file, out e) && e.Value != null && e.StampUtc == stamp)
                    return (T)e.Value;   // unchanged since we loaded it

                T value = null;
                if (stamp != DateTime.MinValue)
                {
                    try
                    {
                        value = OSWData.Load<T>(Tool, file);
                    }
                    catch (Exception ex)
                    {
                        // A typo in a hand-edited file lands here. We keep running on
                        // defaults and leave your file untouched so you can fix it.
                        Warn("'" + file + ".json' could not be read (" + ex.Message +
                             "). Using defaults until it is fixed.");
                    }
                }

                if (value == null) value = makeDefault();
                if (fix != null) value = fix(value);

                _cache[file] = new Entry { Value = value, StampUtc = stamp };
                return value;
            }
        }

        private static void SaveCached<T>(string file, T value) where T : class
        {
            lock (SyncRoot)
            {
                OSWData.Save(Tool, file, value);
                _cache[file] = new Entry { Value = value, StampUtc = StampOf(file) };
            }
        }

        // ── Config ───────────────────────────────────────────────────────

        /// <summary>The shared live config. READ ONLY - use LoadConfigCopy to edit.</summary>
        public static BueTargetConfig LoadConfig(string targetId)
        {
            return LoadCached(
                ConfigFile(targetId),
                () => new BueTargetConfig { TargetId = SafeId(targetId) },
                c => { BlowUpEmoteEngine.NormalizeConfig(c, targetId); return c; });
        }

        /// <summary>A private copy of the config that is safe to edit. Pass it to SaveConfig().</summary>
        public static BueTargetConfig LoadConfigCopy(string targetId)
        {
            BueTargetConfig live = LoadConfig(targetId);
            string json = JsonConvert.SerializeObject(live);
            BueTargetConfig copy = JsonConvert.DeserializeObject<BueTargetConfig>(json);
            return copy ?? new BueTargetConfig { TargetId = SafeId(targetId) };
        }

        public static void SaveConfig(string targetId, BueTargetConfig config)
        {
            if (config == null) return;
            BlowUpEmoteEngine.NormalizeConfig(config, targetId);
            SaveCached(ConfigFile(targetId), config);
        }

        // ── State (fill level, leaderboards, pops) ───────────────────────

        /// <summary>
        /// The current state, read FRESH from the file every time.
        ///
        /// WHY NOT CACHE IT LIKE THE CONFIG? Start, Cancel, Set Emote and the chat
        /// action are separate Streamer.bot actions. Depending on how Streamer.bot
        /// loads the DLL each action may hold its own copy of this class, and a
        /// cached copy that went stale could undo a Cancel. The file is tiny, so
        /// reading it each time costs nothing and means every action always sees
        /// what the last one saved. Change the returned object only inside
        /// lock (SyncRoot), then call SaveState().
        /// </summary>
        public static BueState LoadState(string targetId)
        {
            return LoadCached(
                StateFile(targetId),
                () => new BueState { TargetId = SafeId(targetId) },
                s => FixState(s, targetId),
                alwaysReload: true);   // see note above: the state file is the single source of truth
        }

        public static void SaveState(string targetId, BueState state)
        {
            if (state == null) return;
            SaveCached(StateFile(targetId), FixState(state, targetId));
        }

        private static BueState FixState(BueState s, string targetId)
        {
            if (string.IsNullOrWhiteSpace(s.TargetId)) s.TargetId = SafeId(targetId);
            if (s.RoundContributors == null)
                s.RoundContributors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (s.AllTimeContributors == null)
                s.AllTimeContributors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (s.LastContributionAtUtc == null)
                s.LastContributionAtUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            if (s.FillPercent < 0) s.FillPercent = 0;
            if (s.FillPercent > 100) s.FillPercent = 100;
            // An old state file (from before rounds existed) with a part-filled balloon
            // means "a round is in progress" - keep it visible.
            if (s.FillPercent > 0) s.Active = true;
            return s;
        }

        // ── Picture cache (emote name -> https picture URL) ──────────────

        // The file stores a plain dictionary; we rebuild it case-insensitive so
        // "KEKW" and "kekw" find the same picture.
        private static Dictionary<string, string> FixImages(Dictionary<string, string> raw)
        {
            var ci = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (raw != null)
                foreach (var kv in raw)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        ci[kv.Key] = kv.Value;
            return ci;
        }

        private static Dictionary<string, string> LoadImagesShared(string targetId)
        {
            return LoadCached(
                ImagesFile(targetId),
                () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                FixImages);
        }

        /// <summary>The known picture URL for an emote name, or "" if none learned yet.</summary>
        public static string GetImage(string targetId, string emoteName)
        {
            if (string.IsNullOrWhiteSpace(emoteName)) return "";
            lock (SyncRoot)
            {
                string url;
                return LoadImagesShared(targetId).TryGetValue(emoteName.Trim(), out url) ? (url ?? "") : "";
            }
        }

        /// <summary>
        /// Remember newly learned pictures. Only touches the disk when something
        /// actually changed, so a busy chat doesn't cause a write per message.
        /// Returns true if anything was added or changed.
        /// </summary>
        public static bool MergeImages(string targetId, Dictionary<string, string> found)
        {
            if (found == null || found.Count == 0) return false;

            lock (SyncRoot)
            {
                Dictionary<string, string> cache = LoadImagesShared(targetId);
                bool changed = false;

                foreach (var kv in found)
                {
                    string old;
                    if (!cache.TryGetValue(kv.Key, out old) || old != kv.Value)
                    {
                        cache[kv.Key] = kv.Value;
                        changed = true;
                    }
                }

                if (!changed) return false;

                // Simple size cap so this file can't grow forever.
                if (cache.Count > 400)
                {
                    var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in found) fresh[kv.Key] = kv.Value;
                    cache = fresh;
                }

                SaveCached(ImagesFile(targetId), cache);
                return true;
            }
        }
    }
}
