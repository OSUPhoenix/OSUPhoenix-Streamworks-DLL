// ============================================================================
//  OSWLogger.cs  —  OSWTools.dll
//  Dual-channel logger for all OSUPhoenix StreamWorks tools.
//
//  PURPOSE:
//    Logs to TWO destinations simultaneously:
//      1. Streamer.bot's built-in log  — visible to ALL developers using SB,
//                                        so they can see OSW activity and spot
//                                        conflicts with their own code.
//      2. OSW log file (C:\OSW\Logs\) — your private detailed diagnostics with
//                                        full timestamps, source tags, exception
//                                        traces, and session separators.
//
//  SETUP (call once in your OnInit / startup action):
//    OSWLogger.Init(CPH);
//    OSWLogger.PurgeOldLogs();
//
//  USAGE (from any C# Execute Code action or DLL method):
//    OSWLogger.Info("SAS",      "Achievement unlocked: GIF Gladiator");
//    OSWLogger.Warn("VendMenu", "Item had no price set, defaulting to 0");
//    OSWLogger.Error("GIFBattle","Prediction failed", ex);
//    OSWLogger.Debug("Felix",   "OpenAI response received: " + reply);
//
//  WHAT OTHER DEVELOPERS SEE IN STREAMER.BOT'S LOG:
//    SB's native log methods are called directly — no custom formatting.
//    The message you pass is exactly what shows up, just like any other SB code.
//    SB handles the timestamp and level label on their end automatically.
//
//  WHAT YOU SEE IN YOUR OSW LOG FILE:
//    [2026-03-29 14:32:01.443] [INFO ] [SAS         ] Achievement unlocked: GIF Gladiator
//    [2026-03-29 14:32:01.891] [ERROR] [GIFBattle   ] Prediction failed
//    [2026-03-29 14:32:01.892] [ERROR] [GIFBattle   ]   >> HttpException: 403 Forbidden
//
//  LOG FILE LOCATION:
//    C:\OSW\Logs\OSW_2026-03-29.log   (one file per day, purged after 30 days)
//
//  ADD TO PROJECT:
//    Place this file in your OSWTools project alongside OSWData.cs, etc.
//    No extra NuGet packages needed — uses System.IO only.
// ============================================================================

using System;
using System.IO;
using System.Text;
using Streamer.bot.Plugin.Interface;  // Required for IInlineInvokeProxy (CPH)

namespace OSWTools
{
    // ── Log Level Enum ────────────────────────────────────────────────────────
    // Ordered lowest → highest severity.
    // SetMinimumLevel() filters out anything below your chosen level.
    // Example: SetMinimumLevel(LogLevel.INFO) silences DEBUG lines in production.
    public enum LogLevel
    {
        VERBOSE = 0,  // Extremely fine detail — step-by-step tracing, loop iterations, etc.
        DEBUG   = 1,  // Diagnostic info — variable values, branches taken, state snapshots
        INFO    = 2,  // Normal operational events — action fired, item redeemed, etc.
        WARN    = 3,  // Unexpected but recoverable — missing config, fallback used, etc.
        ERROR   = 4   // Something broke and needs attention
    }


    // ── OSWLogger ─────────────────────────────────────────────────────────────
    // Static class — no instantiation needed. Call OSWLogger.Info(...) directly.
    public static class OSWLogger
    {
        // ── Streamer.bot CPH Reference ────────────────────────────────────────
        // Stored once via Init(CPH) and used to forward messages to SB's log.
        // Marked volatile so all threads always see the latest value.
        private static volatile IInlineInvokeProxy _cph = null;


        // ── Settings ─────────────────────────────────────────────────────────
        private static readonly string LogFolder =
    System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "OSWData", "Logs");

        // Default is DEBUG — change to INFO for production to reduce noise,
        // or VERBOSE when you need every last detail during a tricky debug session.
        private static LogLevel _minimumLevel = LogLevel.INFO;

        // Logs older than this many days are deleted by PurgeOldLogs().
        // 30 days gives you a solid month of history to look back on.
        private static int _maxLogAgeDays = 45;


        // ── Thread Safety ─────────────────────────────────────────────────────
   
        // This lock object ensures only one thread writes to the file at a time,
        // preventing garbled or lost lines.
        private static readonly object _writeLock = new object();


        // ── Session Tracking ─────────────────────────────────────────────────
        // Tracks whether we've written the session header yet in this SB session.
        // Because this is a static field, it resets to false every time
        // Streamer.bot restarts and reloads the DLL.
        private static bool _sessionStarted = false;


        // ── Computed Log File Path ────────────────────────────────────────────
        // Generates today's log file name dynamically.
        // Each new calendar day automatically gets its own file.
        private static string LogFilePath =>
            Path.Combine(LogFolder, $"OSW_{DateTime.Now:yyyy-MM-dd}.log");


        // =====================================================================
        //  PUBLIC API  —  The methods you'll call from your tools
        // =====================================================================

        /// <summary>
        /// CALL THIS FIRST in your OnInit / startup action.
        /// Hands the logger a reference to CPH so it can forward messages to
        /// Streamer.bot's built-in log alongside the OSW file log.
        ///
        /// Example (in your startup C# Execute Code action):
        ///   OSWLogger.Init(CPH);
        ///   OSWLogger.PurgeOldLogs();
        ///   OSWLogger.Info("OSW", "Streamer.bot loaded — OSW tools ready");
        /// </summary>
        public static void Init(IInlineInvokeProxy cph)
        {
            _cph = cph;
        }

        /// <summary>
        /// Extremely fine detail — step-by-step tracing, loop values, raw API responses.
        /// Maps to CPH.LogVerbose() in Streamer.bot.
        /// Example: OSWLogger.Verbose("GIFBattle", "Checking entry: " + userName);
        /// </summary>
        public static void Verbose(string source, string message)
            => Write(LogLevel.VERBOSE, source, message);

        /// <summary>
        /// Fine-grained diagnostic info. Use while building/debugging.
        /// Maps to CPH.LogDebug() in Streamer.bot.
        /// Example: OSWLogger.Debug("GIFBattle", "Prediction ID = " + id);
        /// </summary>
        public static void Debug(string source, string message)
            => Write(LogLevel.DEBUG, source, message);

        /// <summary>
        /// Normal operational events. Use for the things you always want to see.
        /// Maps to CPH.LogInfo() in Streamer.bot.
        /// Example: OSWLogger.Info("SAS", "Achievement unlocked: GIF Gladiator");
        /// </summary>
        public static void Info(string source, string message)
            => Write(LogLevel.INFO, source, message);

        /// <summary>
        /// Something unexpected but the tool kept going.
        /// Maps to CPH.LogWarn() in Streamer.bot.
        /// Example: OSWLogger.Warn("VendMenu", "Price missing, defaulting to 0");
        /// </summary>
        public static void Warn(string source, string message)
            => Write(LogLevel.WARN, source, message);

        /// <summary>
        /// Something broke. Optionally pass the Exception for a full stack trace.
        /// Maps to CPH.LogError() in Streamer.bot.
        /// The exception detail (stack trace, inner exception) goes to the OSW
        /// file only — no need to flood SB's log with traces other devs don't need.
        /// Example: OSWLogger.Error("Felix", "OpenAI call failed", ex);
        /// </summary>
        public static void Error(string source, string message, Exception ex = null)
        {
            // The main error message goes to both channels via Write()
            Write(LogLevel.ERROR, source, message);

            // Exception detail goes to the OSW file only (via AppendLine directly)
            // so SB's log stays readable for other developers.
            if (ex != null)
            {
                string prefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [ERROR] [{source,-12}] ";
                AppendLine(prefix + $"  >> {ex.GetType().Name}: {ex.Message}");
                if (!string.IsNullOrEmpty(ex.StackTrace))
                    AppendLine(prefix + $"  >> Stack: {ex.StackTrace.Trim()}");

                // Also log inner exceptions if present (common with async/await failures)
                if (ex.InnerException != null)
                    AppendLine(prefix + $"  >> Inner: {ex.InnerException.Message}");
            }
        }

        /// <summary>
        /// Writes a blank separator line with an optional label.
        /// Useful for grouping related log lines together visually.
        /// Example: OSWLogger.Section("GIF Battle Round 2");
        /// </summary>
        public static void Section(string label = "")
        {
            // Build a divider that optionally includes the label in the middle
            string divider = string.IsNullOrEmpty(label)
                ? new string('─', 60)
                : $"── {label} " + new string('─', Math.Max(0, 55 - label.Length));

            // Write raw (no level/source prefix) so it reads cleanly in the file
            WriteRaw(divider);
        }

        /// <summary>
        /// Change the minimum log level at runtime.
        /// OSWLogger.SetMinimumLevel(LogLevel.INFO) to suppress DEBUG lines.
        /// OSWLogger.SetMinimumLevel(LogLevel.DEBUG) to see everything.
        /// </summary>
        public static void SetMinimumLevel(LogLevel level)
            => _minimumLevel = level;

        /// <summary>
        /// Deletes log files older than _maxLogAgeDays.
        /// Call this once at Streamer.bot startup (e.g. from your OnInit action).
        /// OSWLogger.PurgeOldLogs();
        /// </summary>
        public static void PurgeOldLogs()
        {
            try
            {
                if (!Directory.Exists(LogFolder)) return;

                DateTime cutoff = DateTime.Now.AddDays(-_maxLogAgeDays);

                foreach (string file in Directory.GetFiles(LogFolder, "OSW_*.log"))
                {
                    // Check the file's last write time against our cutoff
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch
            {
                // Silent fail — log cleanup should never crash a tool
            }
        }


        // =====================================================================
        //  PRIVATE INTERNALS  —  The actual write logic
        // =====================================================================

        /// <summary>
        /// Core write method. Sends to BOTH Streamer.bot's log and the OSW file.
        /// </summary>
        private static void Write(LogLevel level, string source, string message)
        {
            // Filter by minimum level first — no point doing any work below threshold
            if (level < _minimumLevel) return;

            // ── Channel 1: Streamer.bot's built-in log ────────────────────────
            // Uses SB's native CPH methods exactly as SB expects — no custom
            // formatting or prefixing. The message is passed through as-is,
            // the same way any other developer's code would call these methods.
            // SB handles the timestamp and level label on their side.
            if (_cph != null)
            {
                try
                {
                    switch (level)
                    {
                        case LogLevel.VERBOSE: _cph.LogVerbose(message); break;
                        case LogLevel.DEBUG:   _cph.LogDebug(message);   break;
                        case LogLevel.INFO:    _cph.LogInfo(message);    break;
                        case LogLevel.WARN:    _cph.LogWarn(message);    break;
                        case LogLevel.ERROR:   _cph.LogError(message);   break;
                    }
                }
                catch
                {
                    // Silent fail — if SB's log method throws for any reason,
                    // we still want the OSW file log to succeed below.
                }
            }

            // ── Channel 2: OSW log file ───────────────────────────────────────
            // Full detail with timestamp, level, and source column.
            // This is where verbosity lives — timestamps, source tags, exception
            // traces, session headers. Everything SB's log can't give you.
            //
            // Format:  [2026-03-29 14:32:01.443] [INFO   ] [SAS         ] Message here
            // {level,-7}   left-pads level name to 7 chars (VERBOSE is the longest).
            // {source,-12} left-pads source to 12 chars — adjust if your tool
            //              names run longer than that.
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] " +
                          $"[{level,-7}] " +
                          $"[{source,-12}] " +
                          message;

            AppendLine(line);
        }

        /// <summary>
        /// Writes a raw line with no formatting prefix (used by Section()).
        /// </summary>
        private static void WriteRaw(string line)
            => AppendLine($"                                      {line}");
        //                  ↑ padding to align with the message column above

        /// <summary>
        /// Handles the actual file I/O. All writes go through here.
        /// The lock ensures thread safety — only one write at a time.
        /// </summary>
        private static void AppendLine(string line)
        {
            // lock(_writeLock) means: "if another thread is already in here,
            // wait until it's done before I enter." This prevents two threads
            // from writing simultaneously and scrambling the file.
            lock (_writeLock)
            {
                try
                {
                    // Make sure the log folder exists (creates it if not)
                    Directory.CreateDirectory(LogFolder);

                    // First write of this session? Add a visible session header.
                    // This makes it easy to see where each SB restart begins
                    // when you're reading through the log.
                    if (!_sessionStarted)
                    {
                        _sessionStarted = true;
                        string header =
                            Environment.NewLine +
                            "╔══════════════════════════════════════════════════════════╗" + Environment.NewLine +
                            $"║  OSW Session Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}                    ║" + Environment.NewLine +
                            "╚══════════════════════════════════════════════════════════╝";

                        File.AppendAllText(LogFilePath, header + Environment.NewLine, Encoding.UTF8);
                    }

                    // Append the formatted line to today's log file
                    File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Silent fail — logging should NEVER crash a tool.
                    // If the log file is locked or the path is bad,
                    // the tool keeps running, it just misses that line.
                }
            }
        }
    }
}
