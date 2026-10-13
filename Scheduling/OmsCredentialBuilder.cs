// OSWTools/Scheduling/OmsCredentialBuilder.cs
//
// ╔══════════════════════════════════════════════════════════════════════════╗
// ║  DEVELOPER TOOL — not compiled into the distributed DLL.                ║
// ║  Exclude from your Release build configuration.                          ║
// ║                                                                          ║
// ║  Run this once when you first set up your Google OAuth credentials,      ║
// ║  and again any time you rotate them.                                     ║
// ╚══════════════════════════════════════════════════════════════════════════╝
//
// HOW TO USE:
//   1. Paste your real Client ID and Client Secret into the fields below
//   2. Run this file (Console App, or call GenerateAndPrint() from a test action)
//   3. Copy the output into OmsCredentials.cs (the gitignore'd file)
//   4. Clear the values from this file and do not commit it
//
// This tool also generates the DpapiEntropy bytes if you need to create them
// for the first time. Run with generateEntropy: true ONCE, copy the output,
// then always use the same entropy bytes — changing them invalidates all
// existing encrypted tokens on all user machines.

using System;
using System.Text;

namespace OSWTools.Scheduling.DevTools
{
    internal static class OmsCredentialBuilder
    {
        // ── INPUT — fill these in, run, then clear them ───────────────────────
        private const string REAL_CLIENT_ID     = ""; // paste your Google Client ID here
        private const string REAL_CLIENT_SECRET = ""; // paste your Google Client Secret here

        // ── Entry point ───────────────────────────────────────────────────────
        // Call this from a temporary SB action or a console app project.
        // Output goes to console (or SB log if called from an action via CPH.LogInfo).
        internal static void GenerateAndPrint(bool generateEntropy = false)
        {
            if (string.IsNullOrEmpty(REAL_CLIENT_ID) || string.IsNullOrEmpty(REAL_CLIENT_SECRET))
            {
                Console.WriteLine("[OmsCredentialBuilder] ERROR: Fill in REAL_CLIENT_ID and REAL_CLIENT_SECRET before running.");
                return;
            }

            // Generate a random XOR key — length should cover the longer of the two strings
            int keyLength = Math.Max(REAL_CLIENT_ID.Length, REAL_CLIENT_SECRET.Length) + 8;
            byte[] xorKey = GenerateRandomBytes(keyLength);

            // Encode both values against the same key
            byte[] encodedId     = XorEncode(REAL_CLIENT_ID,     xorKey);
            byte[] encodedSecret = XorEncode(REAL_CLIENT_SECRET, xorKey);

            Console.WriteLine("// ── Paste this into OmsCredentials.cs ──────────────────────────");
            Console.WriteLine();
            Console.WriteLine("private static readonly byte[] _xorKey = new byte[]");
            Console.WriteLine("{");
            Console.WriteLine("    " + FormatByteArray(xorKey));
            Console.WriteLine("};");
            Console.WriteLine();
            Console.WriteLine("private static readonly byte[] _idParts = new byte[]");
            Console.WriteLine("{");
            Console.WriteLine("    " + FormatByteArray(encodedId));
            Console.WriteLine("};");
            Console.WriteLine();
            Console.WriteLine("private static readonly byte[] _secParts = new byte[]");
            Console.WriteLine("{");
            Console.WriteLine("    " + FormatByteArray(encodedSecret));
            Console.WriteLine("};");

            if (generateEntropy)
            {
                // 32 random bytes — enough for DPAPI entropy, not guessable
                byte[] entropy = GenerateRandomBytes(32);
                Console.WriteLine();
                Console.WriteLine("// ── Generate entropy ONCE and never change ──────────────────────");
                Console.WriteLine("internal static readonly byte[] DpapiEntropy = new byte[]");
                Console.WriteLine("{");
                Console.WriteLine("    " + FormatByteArray(entropy));
                Console.WriteLine("};");
            }

            Console.WriteLine();
            Console.WriteLine("// ── Verify round-trip (sanity check) ───────────────────────────");
            // Decode and verify immediately
            string decodedId     = XorDecode(encodedId,     xorKey);
            string decodedSecret = XorDecode(encodedSecret, xorKey);
            Console.WriteLine($"// Client ID match:     {decodedId     == REAL_CLIENT_ID}");
            Console.WriteLine($"// Client Secret match: {decodedSecret == REAL_CLIENT_SECRET}");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static byte[] XorEncode(string input, byte[] key)
        {
            byte[] data   = Encoding.UTF8.GetBytes(input);
            byte[] result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = (byte)(data[i] ^ key[i % key.Length]);
            return result;
        }

        private static string XorDecode(byte[] data, byte[] key)
        {
            byte[] result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = (byte)(data[i] ^ key[i % key.Length]);
            return Encoding.UTF8.GetString(result);
        }

        private static byte[] GenerateRandomBytes(int length)
        {
            // Using System.Security.Cryptography for a proper random source,
            // not System.Random which is predictable
            byte[] bytes = new byte[length];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return bytes;
        }

        private static string FormatByteArray(byte[] bytes)
        {
            // Format as "0xAB, 0xCD, ..." with 16 bytes per line for readability
            var sb = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0 && i % 16 == 0)
                    sb.Append("\n    ");
                sb.Append($"0x{bytes[i]:X2}");
                if (i < bytes.Length - 1)
                    sb.Append(", ");
            }
            return sb.ToString();
        }
    }
}
