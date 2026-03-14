using System;
using Streamer.bot.Plugin.Interface;

namespace OSWTools
{
    /// <summary>
    /// OSWLib — the root of all OSW tool functionality.
    ///
    /// This is a partial class. Every file in Core/, Extensions/, and Versioning/
    /// is another piece of this same class, split into files by category.
    /// All of them share the _CPH, _ToolName, and _DebugMode fields below.
    ///
    /// USAGE in any Streamer.bot inline script:
    ///
    ///   using OSWTools;
    ///
    ///   public class CPHInline
    ///   {
    ///       public bool Execute()
    ///       {
    ///           var lib = new OSWLib(CPH, "My Tool Name");
    ///           lib.LogInfo("Tool started.");
    ///           return true;
    ///       }
    ///   }
    ///
    /// IMPORTANT — STA threading:
    ///   OSWLib itself has no thread affinity. If you open a WinForms window,
    ///   wrap it in StaThread.Run() from OSWTools.Utilities as before.
    ///   CPH calls (LogInfo, GetGlobal, etc.) are safe from any thread.
    /// </summary>
    public partial class OSWLib
    {
        // ── Shared fields ─────────────────────────────────────────────────────────
        // Every partial class file in this project can access these directly.

        /// <summary>The live Streamer.bot CPH proxy. Never null after construction.</summary>
        private readonly IInlineInvokeProxy _CPH;

        /// <summary>
        /// The name of the tool using this instance.
        /// Prepended to all log messages: "[My Tool] message here"
        /// </summary>
        private readonly string _ToolName;

        /// <summary>
        /// When true, LogDebug() calls write to the SB log.
        /// Controlled by the global var "osw_DebugMode" (persisted).
        /// </summary>
        private readonly bool _DebugMode;

        // ── Static initialization (once per SB session) ───────────────────────────
        private static bool _staticInitialized = false;
        private static readonly object _initLock = new object();

        // ── Constructors ──────────────────────────────────────────────────────────

        /// <summary>
        /// Primary constructor. Pass CPH and your tool's name.
        /// </summary>
        /// <param name="cph">The CPH object injected by Streamer.bot.</param>
        /// <param name="toolName">
        ///   The display name of the tool creating this instance.
        ///   Used as a prefix in all log output, e.g. "[Achievement System]".
        /// </param>
        public OSWLib(IInlineInvokeProxy cph, string toolName)
        {
            if (cph == null)      throw new ArgumentNullException("cph");
            if (toolName == null) throw new ArgumentNullException("toolName");

            _CPH      = cph;
            _ToolName = toolName;
            _DebugMode = cph.GetGlobalVar<bool>("osw_DebugMode", true);

            InitializeStaticComponents();
        }

        /// <summary>
        /// Convenience constructor when you don't need tool-name logging.
        /// Defaults to "OSWTools" as the tool name.
        /// </summary>
        public OSWLib(IInlineInvokeProxy cph)
            : this(cph, "OSWTools")
        {
        }

        // ── Static initialization ─────────────────────────────────────────────────

        /// <summary>
        /// Runs once per Streamer.bot session regardless of how many tools
        /// construct an OSWLib instance. Thread-safe via double-check locking.
        /// Add any one-time setup here (e.g. ensuring OSWData folder exists).
        /// </summary>
        private void InitializeStaticComponents()
        {
            // Fast path — already done
            if (_staticInitialized) return;

            lock (_initLock)
            {
                // Second check inside the lock in case another thread beat us here
                if (_staticInitialized) return;

                try
                {
                    // Ensure the OSWData root folder exists so tools can write immediately
                    OSWTools.Data.FileManager.EnsureRootFolder();
                    _staticInitialized = true;
                }
                catch (Exception ex)
                {
                    // Silent failure — individual methods handle missing init gracefully.
                    // We still mark as initialized so we don't retry on every construction.
                    _CPH.LogWarn("[OSWTools] Static init failed: " + ex.Message);
                    _staticInitialized = true;
                }
            }
        }
    }
}
