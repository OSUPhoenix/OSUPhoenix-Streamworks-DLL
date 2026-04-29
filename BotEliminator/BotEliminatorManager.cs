using System;
using System.Collections.Generic;
using System.Linq;
using OSWTools.Data;
using OSWTools.Utilities;
using Streamer.bot.Plugin.Interface;

namespace OSWTools.BotEliminator
{
    public class BotEliminatorManager
    {
        private const string ToolName = "BotEliminator";
        private const string FileName = "exclusions";

        private readonly IInlineInvokeProxy _CPH;

private BotEliminatorData _cache;

        public BotEliminatorManager(IInlineInvokeProxy cph)
        {
            _CPH = cph ?? throw new ArgumentNullException("cph");
        }

        // ── Data access ───────────────────────────────────────────────────────────

        public BotEliminatorData Load()
        {
            var cached = _cache;
            if (cached != null) return cached;
            MigrateFromGlobalsIfNeeded();
            var loaded = OSWData.LoadOrDefault<BotEliminatorData>(ToolName, FileName, new BotEliminatorData());
            _cache = loaded;
            return loaded;
        }

 public void Save(BotEliminatorData data)
        {
            OSWData.Save<BotEliminatorData>(ToolName, FileName, data);
            _cache = null;
        }

 public void Reload()
        {
            _cache = null;
        }
        // ── Public API ────────────────────────────────────────────────────────────

        public bool IsExcluded(string user)
        {
            if (string.IsNullOrWhiteSpace(user))
                return false;

            string platform = ResolvePlatform();
            if (string.IsNullOrEmpty(platform))
            {
                _CPH.LogWarn("[Bot Eliminator] Could not determine platform — allowing user through.");
                return false;
            }

            string clean = CleanHandle(user);
            var list = GetPlatformList(Load(), platform);
            bool excluded = list.Any(u => string.Equals(u, clean, StringComparison.OrdinalIgnoreCase));

            if (excluded)
                _CPH.LogInfo($"[Bot Eliminator] '{clean}' is excluded on {platform} — halting.");
            else
                _CPH.LogDebug($"[Bot Eliminator] '{clean}' is allowed on {platform} — continuing.");

            return excluded;
        }

        public AddUserResult AddUser(string platformInput, string usernameInput)
        {
            string platform = NormalizePlatform(platformInput);
            if (string.IsNullOrEmpty(platform))
                return new AddUserResult(false, $"Unknown platform '{platformInput}'. Supported: twitch, youtube, kick");

            string target = CleanHandle(usernameInput);
            if (string.IsNullOrEmpty(target))
                return new AddUserResult(false, "Username cannot be empty.");

            var data = Load();
            var list = GetPlatformList(data, platform);

            if (list.Any(u => string.Equals(u, target, StringComparison.OrdinalIgnoreCase)))
                return new AddUserResult(false, $"{target} is already on the {platform} exclusion list.");

            list.Add(target);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            Save(data);

            _CPH.LogInfo($"[Bot Eliminator] Added '{target}' to {platform} exclusion list.");
            return new AddUserResult(true, $"{target} has been added to the {platform} exclusion list.");
        }

        public void ShowSettings()
        {
            DpiHelper.EnsureDpiAware();
            StaThread.Run(() =>
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

                var data = Load();
                using (var form = new BotEliminatorForm(data))
                {
                    form.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
                    if (form.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        Save(form.Result);
                        _CPH.LogInfo("[Bot Eliminator] Exclusion lists saved.");
                    }
                }
            });
        }

        // ── Platform resolution ───────────────────────────────────────────────────

        public string ResolvePlatform()
        {
            string platform = null;

            if (platform == null && _CPH.TryGetArg("platform", out string platformArg))
                platform = NormalizePlatform(platformArg);

            if (platform == null && _CPH.TryGetArg("eventSource", out string eventSource))
            {
                if (string.Equals(eventSource.Trim(), "command", StringComparison.OrdinalIgnoreCase))
                {
                    if (_CPH.TryGetArg("commandSource", out string commandSource))
                        platform = NormalizePlatform(commandSource);
                }
                else
                {
                    platform = NormalizePlatform(eventSource);
                }
            }

            if (platform == null)
                platform = NormalizePlatform(_CPH.GetSource().ToString());

            return platform;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private List<string> GetPlatformList(BotEliminatorData data, string platform)
        {
            switch (platform)
            {
                case "Twitch":  return data.Twitch;
                case "YouTube": return data.YouTube;
                case "Kick":    return data.Kick;
                default:        return new List<string>();
            }
        }

        private static string CleanHandle(string u)
        {
            if (string.IsNullOrWhiteSpace(u)) return "";
            return u.Trim().TrimStart('@').ToLowerInvariant();
        }

        private static string NormalizePlatform(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return null;

            switch (source.Trim().ToLowerInvariant())
            {
                case "twitch":
                case "eventsource.twitch":
                    return "Twitch";
                case "youtube":
                case "eventsource.youtube":
                case "eventsource.youtubechat":
                    return "YouTube";
                case "kick":
                case "eventsource.kick":
                    return "Kick";
            }

            string s = source.Trim();
            if (s.Equals("Twitch",  StringComparison.OrdinalIgnoreCase)) return "Twitch";
            if (s.Equals("YouTube", StringComparison.OrdinalIgnoreCase)) return "YouTube";
            if (s.Equals("Kick",    StringComparison.OrdinalIgnoreCase)) return "Kick";

            return null;
        }

        // ── Migration ─────────────────────────────────────────────────────────────

        private void MigrateFromGlobalsIfNeeded()
        {
            if (OSWData.Exists(ToolName, FileName))
                return;

            string t = _CPH.GetGlobalVar<string>("OSUP_Exclude_Twitch",  true) ?? "";
            string y = _CPH.GetGlobalVar<string>("OSUP_Exclude_YouTube", true) ?? "";
            string k = _CPH.GetGlobalVar<string>("OSUP_Exclude_Kick",    true) ?? "";

            if (string.IsNullOrWhiteSpace(t) && string.IsNullOrWhiteSpace(y) && string.IsNullOrWhiteSpace(k))
                return;

            var sep = new[] { "\r\n", "\n", "," };
            var data = new BotEliminatorData
            {
                Twitch  = ParseGlobalList(t, sep),
                YouTube = ParseGlobalList(y, sep),
                Kick    = ParseGlobalList(k, sep)
            };

            OSWData.Save<BotEliminatorData>(ToolName, FileName, data);
            _CPH.LogInfo("[Bot Eliminator] Migrated exclusion lists from globals to OSWData JSON.");
        }

        private static List<string> ParseGlobalList(string raw, string[] separators)
        {
            return raw
                .Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(CleanHandle)
                .Where(u => !string.IsNullOrEmpty(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public class AddUserResult
    {
        public bool Success { get; }
        public string Message { get; }

        public AddUserResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }
    }
}
