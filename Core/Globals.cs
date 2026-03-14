namespace OSWTools
{
    /// <summary>
    /// Typed, safe wrappers for Streamer.bot global variables.
    ///
    /// WHY THIS EXISTS:
    /// Every script previously had its own version of:
    ///   CPH.GetGlobalVar&lt;T&gt;(key, true) ?? fallback
    /// with inconsistent naming (SafeGetBool, GetGlobalInt, etc.).
    /// These methods standardize that pattern across all OSW tools.
    ///
    /// PERSISTED vs SESSION:
    ///   persisted = true  → survives Streamer.bot restart (saved to disk by SB)
    ///   persisted = false → lives only for the current SB session (in-memory)
    ///   Most settings should be persisted. Runtime state (e.g. "is rotation active")
    ///   should be session-only.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///
    ///   // Get with fallback — never throws, never returns null for value types
    ///   int    delay   = lib.GetGlobal("ClipPlayer_DelaySeconds", 5);
    ///   bool   active  = lib.GetGlobal("ClipPlayer_Active",       false);
    ///   string source  = lib.GetGlobal("ClipPlayer_SourceName",   "");
    ///
    ///   // Set — persisted by default
    ///   lib.SetGlobal("ClipPlayer_DelaySeconds", 10);
    ///   lib.SetGlobal("ClipPlayer_Active", true, persisted: false); // session only
    /// </summary>
    public partial class OSWLib
    {
        // ── Get ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets a persisted global variable, returning <paramref name="fallback"/>
        /// if the key doesn't exist or the value is null.
        /// </summary>
        public T GetGlobal<T>(string key, T fallback = default(T))
        {
            try
            {
                T value = _CPH.GetGlobalVar<T>(key, true);
                if (value == null) return fallback;
                return value;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Gets a session-only (non-persisted) global variable.
        /// Use for runtime state that should reset when SB restarts.
        /// </summary>
        public T GetGlobalSession<T>(string key, T fallback = default(T))
        {
            try
            {
                T value = _CPH.GetGlobalVar<T>(key, false);
                if (value == null) return fallback;
                return value;
            }
            catch
            {
                return fallback;
            }
        }

        // ── Set ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets a global variable. Persisted by default (survives SB restart).
        /// Pass persisted: false for session-only runtime state.
        /// </summary>
        public void SetGlobal<T>(string key, T value, bool persisted = true)
        {
            try
            {
                _CPH.SetGlobalVar(key, value, persisted);
            }
            catch
            {
                LogError("SetGlobal failed for key: " + key);
            }
        }

        // ── Unset ─────────────────────────────────────────────────────────────────

        /// <summary>Removes a persisted global variable.</summary>
        public void UnsetGlobal(string key)
        {
            try { _CPH.UnsetGlobalVar(key, true); }
            catch { /* best effort */ }
        }

        /// <summary>Removes a session-only global variable.</summary>
        public void UnsetGlobalSession(string key)
        {
            try { _CPH.UnsetGlobalVar(key, false); }
            catch { /* best effort */ }
        }
    }
}
