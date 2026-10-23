using System;
using System.Collections.Generic;

namespace Game.Execution
{
    // Tiny reader for the plan contract {"actions":[{"type":"X","target":"y"}]}.
    // Internal on purpose: no dependency on AI DTOs, no packages, never throws
    // outward (throws only inward to LoadPlan's try/catch on malformed input).
    internal sealed class PlanEntry
    {
        public string Type;
        public string Target;
    }

    internal static class PlanJsonReader
    {
        public static List<PlanEntry> Read(string json)
        {
            var entries = new List<PlanEntry>();
            if (string.IsNullOrEmpty(json)) return entries;
            int i = json.IndexOf('[');
            while (i >= 0)
            {
                int open = json.IndexOf('{', i);
                if (open < 0) break;
                int close = json.IndexOf('}', open);
                if (close < 0) break;
                string obj = json.Substring(open + 1, close - open - 1);
                string type = Field(obj, "type");
                if (!string.IsNullOrEmpty(type))
                    entries.Add(new PlanEntry { Type = type, Target = Field(obj, "target") });
                i = close + 1;
                if (json.IndexOf(']', i) >= 0 && json.IndexOf('{', i) < 0) break;
            }
            return entries;
        }

        private static string Field(string obj, string name)
        {
            string key = "\"" + name + "\"";
            int k = obj.IndexOf(key, StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = obj.IndexOf(':', k + key.Length);
            if (colon < 0) return null;
            int q1 = obj.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = obj.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return obj.Substring(q1 + 1, q2 - q1 - 1);
        }
    }
}
