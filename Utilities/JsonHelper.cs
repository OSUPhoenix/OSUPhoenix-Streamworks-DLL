using System;
using Newtonsoft.Json;

namespace OSWTools.Utilities
{
    /// <summary>
    /// Convenience wrappers around Newtonsoft.Json for common patterns
    /// found across OSW scripts.
    ///
    /// USAGE:
    ///   // Deep copy any serializable object
    ///   var copy = JsonHelper.Clone(original);
    ///
    ///   // Safe deserialize — returns fallback instead of throwing
    ///   var list = JsonHelper.SafeDeserialize<List<string>>(json, new List<string>());
    ///
    ///   // Compact serialize
    ///   string json = JsonHelper.Serialize(myObject);
    ///
    ///   // Pretty-print serialize
    ///   string pretty = JsonHelper.SerializePretty(myObject);
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// Deep-copies an object by serializing then deserializing it.
        /// This is the safest way to clone a complex object graph in .NET 4.7.2.
        /// Returns null if the input is null.
        /// </summary>
        public static T Clone<T>(T obj)
        {
            if (obj == null) return default(T);
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(obj));
        }

        /// <summary>
        /// Deserializes JSON into <typeparamref name="T"/>.
        /// Returns <paramref name="fallback"/> if the JSON is null/empty or
        /// if deserialization throws — never propagates exceptions.
        /// </summary>
        public static T SafeDeserialize<T>(string json, T fallback = default(T))
        {
            if (string.IsNullOrWhiteSpace(json)) return fallback;
            try
            {
                var result = JsonConvert.DeserializeObject<T>(json);
                return result != null ? result : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Serializes an object to a compact (no whitespace) JSON string.
        /// Returns "null" if the object is null, never throws.
        /// </summary>
        public static string Serialize(object obj)
        {
            try
            {
                return JsonConvert.SerializeObject(obj, Formatting.None);
            }
            catch
            {
                return "null";
            }
        }

        /// <summary>
        /// Serializes an object to an indented (human-readable) JSON string.
        /// Useful for writing config files. Never throws.
        /// </summary>
        public static string SerializePretty(object obj)
        {
            try
            {
                return JsonConvert.SerializeObject(obj, Formatting.Indented);
            }
            catch
            {
                return "null";
            }
        }
    }
}
