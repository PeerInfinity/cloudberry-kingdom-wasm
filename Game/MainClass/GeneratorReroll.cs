using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudberryKingdom
{
    /// <summary>
    /// ck-level/2 piece rerolls (the cloudberry substrate's R14: every claimed item gate is real).
    ///
    /// "rerolls": [a_0, a_1, …] — piece k is made from the reroll seed RerollSeed(base, k, a_k) when a_k &gt; 0 (MakeLevel
    /// re-seeds the level's Rand just before that piece; the pieces after it draw on from there). a_k = 0 = the piece as
    /// the seed makes it. The document records each rerolled piece's attempt and seed (seedPieces[].reroll).
    ///
    /// "reroll": true | {maxAttempts} — the search. Piece by piece, in order: a piece is ACCEPTED when its replay with
    /// exactly S_k held passes and every control (S_k minus one item new in S_k) FAILS; otherwise its attempt goes up (and
    /// every later piece's goes back to 0, since a piece's reroll changes the pieces after it). A piece that is still
    /// refused after maxAttempts attempts ends the search with the named refusal "RerollExhausted". The search returns
    /// the document of the RESOLVED request (the request without "reroll", with "rerolls" set): byte for byte what that
    /// resolved request generates, so a host that stores the resolved request regenerates the same document.
    /// </summary>
    public static partial class GeneratorApi
    {
        /// <summary>The reroll seed scheme (recorded in the document).</summary>
        public const string RerollScheme = "ck-reroll/1";
        public const int DefaultRerollMaxAttempts = 16;

        /// <summary>
        /// ck-reroll/1: the seed of piece k's attempt a (a &gt; 0) from the level's base seed. A fixed integer mix
        /// (murmur3's finaliser over base, k, a); non-negative.
        /// </summary>
        public static int RerollSeed(int baseSeed, int piece, int attempt)
        {
            unchecked
            {
                uint x = (uint)baseSeed * 0x9E3779B1u ^ (uint)(piece + 1) * 0x85EBCA77u ^ (uint)attempt * 0xC2B2AE3Du;
                x ^= x >> 16; x *= 0x85EBCA6Bu;
                x ^= x >> 13; x *= 0xC2B2AE35u;
                x ^= x >> 16;
                return (int)(x & 0x7FFFFFFFu);
            }
        }

        /// <summary>
        /// A JSON string literal. (JsonSerializer is not used: the browser build is trimmed, and reflection-based
        /// serialization is disabled there — JsonSerializerIsReflectionDisabled.)
        /// </summary>
        public static string JsonStr(string s)
        {
            return s == null ? "null" : "\"" + JsonEncodedText.Encode(s, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";
        }

        public static string JsonStrings(IEnumerable<string> list)
        {
            return "[" + string.Join(",", list.Select(JsonStr)) + "]";
        }

        /// <summary>The last search's report (JSON): attempts per piece, generations tried, refusal, ms.</summary>
        public static string LastRerollReport = "null";
        /// <summary>The last search's time (not in the document: documents are deterministic).</summary>
        public static double LastRerollSearchMs, LastRerollTotalMs;

        /// <summary>The request as JSON with "reroll" removed and "rerolls" set; extra overrides for the search's own probes.</summary>
        static string ResolvedRequest(string raw, int[] rerolls, Dictionary<string, JsonNode> overrides)
        {
            JsonObject o = JsonNode.Parse(raw).AsObject();
            o.Remove("reroll");
            o.Remove("rerolls");
            var arr = new JsonArray();
            foreach (int r in rerolls) arr.Add(JsonValue.Create(r));
            o["rerolls"] = arr;
            if (overrides != null)
                foreach (var kv in overrides) { o.Remove(kv.Key); o[kv.Key] = kv.Value.DeepClone(); }
            return o.ToJsonString();
        }

        class RerollVerdict { public bool Accepted; public string Why; }

        /// <summary>Reads a probe document's verify section: the first piece that is not accepted, or -1.</summary>
        static int FirstRefusedPiece(string doc, out RerollVerdict verdict, out int pieces)
        {
            verdict = null;
            pieces = 0;
            using (var d = JsonDocument.Parse(doc))
            {
                JsonElement root = d.RootElement, err;
                if (root.TryGetProperty("error", out err)) throw new InvalidOperationException("reroll probe failed: " + err.GetString());
                JsonElement list = root.GetProperty("verify").GetProperty("pieces");
                pieces = list.GetArrayLength();
                foreach (JsonElement p in list.EnumerateArray())
                {
                    var why = new List<string>();
                    if (!p.GetProperty("withS").GetProperty("passed").GetBoolean())
                        why.Add("withS " + p.GetProperty("withS").GetProperty("failedBy").GetString());
                    foreach (JsonElement c in p.GetProperty("controls").EnumerateArray())
                        if (c.GetProperty("result").GetProperty("passed").GetBoolean())
                            why.Add("control without " + c.GetProperty("drop").GetString() + " still passes");
                    if (why.Count > 0)
                    {
                        verdict = new RerollVerdict { Accepted = false, Why = string.Join("; ", why) };
                        return p.GetProperty("piece").GetInt32();
                    }
                }
            }
            return -1;
        }

        static string GenerateWithReroll(Args a)
        {
            var sw = Stopwatch.StartNew();
            int max = a.RerollMaxAttempts;
            // Pieces: known from the request when given as a list or an int; else read from the first probe.
            int n = a.NumPieces > 0 ? a.NumPieces : 1;
            var rerolls = new int[n];
            // The probes only need the verifier.
            var probe = new Dictionary<string, JsonNode>
            {
                { "verify", JsonValue.Create(true) }, { "replay", JsonValue.Create(true) }, { "coins", JsonValue.Create(false) },
                { "trace", JsonValue.Create(false) }, { "settings", JsonValue.Create(false) },
            };
            int generations = 0;
            int totalCap = max * n * 4;
            var refusedWhy = new List<string>();
            string refusal = null;
            while (true)
            {
                generations++;
                string doc = Generate(ResolvedRequest(a.Raw, rerolls, probe));
                RerollVerdict v;
                int pieces;
                int k = FirstRefusedPiece(doc, out v, out pieces);
                if (pieces != n) throw new InvalidOperationException("reroll: the level has " + pieces + " pieces, the request " + n);
                if (k < 0) break;
                refusedWhy.Add("piece " + k + " attempt " + rerolls[k] + ": " + v.Why);
                rerolls[k]++;
                for (int j = k + 1; j < n; j++) rerolls[j] = 0;
                if (rerolls[k] >= max)
                {
                    refusal = "RerollExhausted: piece " + k + " refused in all " + max + " attempts (last: " + v.Why + ")";
                    break;
                }
                if (generations >= totalCap)
                {
                    refusal = "RerollExhausted: " + generations + " generations without accepting every piece (last: piece " + k + ": " + v.Why + ")";
                    break;
                }
            }

            var report = new StringBuilder();
            report.Append("{\"scheme\":\"").Append(RerollScheme).Append("\",\"maxAttempts\":").Append(max)
                .Append(",\"generations\":").Append(generations).Append(",\"rerolls\":[").Append(string.Join(",", rerolls)).Append(']')
                .Append(",\"refusal\":").Append(refusal == null ? "null" : JsonStr(refusal))
                .Append(",\"refused\":").Append(JsonStrings(refusedWhy)).Append('}');
            LastRerollSearchMs = sw.Elapsed.TotalMilliseconds;

            double searchMs = LastRerollSearchMs;
            if (refusal != null)
            {
                LastRerollTotalMs = searchMs;
                LastRerollReport = report.ToString();
                throw new RerollExhaustedException(refusal);
            }
            string resolved = Generate(ResolvedRequest(a.Raw, rerolls, null));
            LastRerollReport = report.ToString();
            LastRerollSearchMs = searchMs;
            LastRerollTotalMs = sw.Elapsed.TotalMilliseconds;
            return resolved;
        }

        public class RerollExhaustedException : Exception
        {
            public RerollExhaustedException(string message) : base(message) { }
        }
    }
}
