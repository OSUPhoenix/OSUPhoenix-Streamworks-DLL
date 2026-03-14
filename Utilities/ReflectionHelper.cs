using System;

namespace OSWTools.Utilities
{
    /// <summary>
    /// Safe property readers for Streamer.bot's anonymous/dynamic objects.
    ///
    /// WHY THIS EXISTS:
    /// Streamer.bot returns many objects (clips, users, channel info) as
    /// opaque types whose properties you can only access via reflection.
    /// Doing this inline every time is verbose and crashes if the property
    /// doesn't exist. These helpers wrap it in a safe try/catch.
    ///
    /// USAGE:
    ///   var clip = ...; // returned from CPH.GetClipsForUser(...)
    ///   string url  = ReflectionHelper.GetString(clip, "ThumbnailUrl");
    ///   bool mature = ReflectionHelper.GetBool(clip,   "IsMature", false);
    ///   double dur  = ReflectionHelper.GetDouble(clip, "Duration",  10.0);
    /// </summary>
    public static class ReflectionHelper
    {
        /// <summary>
        /// Reads a string property from an object by name.
        /// Returns <paramref name="fallback"/> if the property doesn't exist,
        /// is null, or throws.
        /// </summary>
        public static string GetString(object obj, string propertyName, string fallback = null)
        {
            try
            {
                var prop = obj?.GetType().GetProperty(propertyName);
                var value = prop?.GetValue(obj);
                return value?.ToString() ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Reads a bool property from an object by name.
        /// Returns <paramref name="fallback"/> if the property doesn't exist or throws.
        /// </summary>
        public static bool GetBool(object obj, string propertyName, bool fallback = false)
        {
            try
            {
                var prop = obj?.GetType().GetProperty(propertyName);
                var value = prop?.GetValue(obj);
                if (value == null) return fallback;
                if (value is bool b) return b;
                if (bool.TryParse(value.ToString(), out bool parsed)) return parsed;
                return fallback;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Reads a double property from an object by name.
        /// Returns <paramref name="fallback"/> if the property doesn't exist or throws.
        /// </summary>
        public static double GetDouble(object obj, string propertyName, double fallback = 0.0)
        {
            try
            {
                var prop = obj?.GetType().GetProperty(propertyName);
                var value = prop?.GetValue(obj);
                if (value == null) return fallback;
                return Convert.ToDouble(value);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Reads an int property from an object by name.
        /// Returns <paramref name="fallback"/> if the property doesn't exist or throws.
        /// </summary>
        public static int GetInt(object obj, string propertyName, int fallback = 0)
        {
            try
            {
                var prop = obj?.GetType().GetProperty(propertyName);
                var value = prop?.GetValue(obj);
                if (value == null) return fallback;
                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Reads the first non-null, non-empty string from a list of candidate
        /// property names. Useful when SB returns objects with inconsistent
        /// property naming across API versions.
        ///
        /// USAGE:
        ///   string login = ReflectionHelper.GetFirstString(clip,
        ///       "BroadcasterLogin", "CreatorLogin", "Login");
        /// </summary>
        public static string GetFirstString(object obj, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                var result = GetString(obj, name);
                if (!string.IsNullOrWhiteSpace(result))
                    return result;
            }
            return null;
        }
    }
}
