using System;
using System.IO;
using UnityEngine;

namespace Game.AI
{
    // Best-effort JSONL sink for the standalone LLM dashboard. Logging is
    // deliberately static so every decision path can use the same schema,
    // without making AI decisions depend on a scene singleton.
    public static class DecisionLogger
    {
        public static bool Enabled = true;
        public static string PathOverride;

        public static void LogBotBrain(string actor, CombatStateSnapshot snapshot, string plan)
        {
            Write(actor, "BotBrain-local", "local", snapshot, plan, null, 0, "botbrain");
        }

        public static void LogLlm(string actor, CombatStateSnapshot snapshot, string plan,
            string model, string endpoint, long latencyMs, string raw, int payloadBytes = 0)
        {
            Write(actor, model, endpoint, snapshot, plan, raw, latencyMs, "llm", payloadBytes);
        }

        // [F53] Mode changes event logging for LLM dashboard / telemetry.
        public static void LogMode(string mode, string persona)
        {
            if (!Enabled) return;
            try
            {
                string line = "{\"ts\":\"" + Escape(DateTime.UtcNow.ToString("o"))
                    + "\",\"event\":\"boss_mode\""
                    + ",\"mode\":\"" + Escape(mode)
                    + "\",\"persona\":\"" + Escape(persona) + "\"}";
                string path = ResolvePath();
                string directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(path, line + "\n");
            }
            catch (Exception) { /* telemetry must never interrupt gameplay */ }
        }

        // [AI-F8][lead] Strategist ask telemetry — until now the strategist
        // was invisible in the jsonl (boss_mode only fires when the persona
        // CHANGES, so a boss stuck in Distance looked like "no data"). Each
        // ask now records the RAW one-word answer, latency, how long the
        // persona had been held, and whether the variety guard overrode the
        // model's choice. This is the evidence trail for balance/demos.
        public static void LogStrategist(string persona, string raw,
            long latencyMs, float heldSeconds, bool forced)
        {
            if (!Enabled) return;
            try
            {
                string line = "{\"ts\":\"" + Escape(DateTime.UtcNow.ToString("o"))
                    + "\",\"event\":\"boss_strategy\""
                    + ",\"persona\":\"" + Escape(persona) + "\""
                    + ",\"raw\":\"" + Escape(raw) + "\""
                    + ",\"latencyMs\":" + latencyMs
                    + ",\"heldSeconds\":" + heldSeconds.ToString("0",
                        System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"forced\":" + (forced ? "true" : "false") + "}";
                string path = ResolvePath();
                string directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(path, line + "\n");
            }
            catch (Exception) { /* telemetry must never interrupt gameplay */ }
        }

        public static string ResolvePath() => string.IsNullOrEmpty(PathOverride)
            ? System.IO.Path.Combine(Application.dataPath, "..", "Logs", "llm-decisions.jsonl")
            : PathOverride;

        // [F38] payloadBytes lands in the jsonl only when the caller measured
        // it (optional param, default 0 -> field omitted). LlmBrain already
        // Debug.Logs it per decision; wiring the jsonl needs one line at the
        // BotController call sites (out of F38's zone).
        private static void Write(string actor, string model, string endpoint,
            CombatStateSnapshot snapshot, string plan, string raw, long latencyMs, string source,
            int payloadBytes = 0)
        {
            if (!Enabled) return;
            try
            {
                string snapshotJson = CombatStateSerializer.ToJson(snapshot);
                string planJson = string.IsNullOrEmpty(plan) ? "{\"actions\":[]}" : plan;
                string rawJson = raw == null ? "null" : "\"" + Escape(raw) + "\"";
                string payloadJson = payloadBytes > 0 ? ",\"payloadBytes\":" + payloadBytes : string.Empty;
                string line = "{\"ts\":\"" + Escape(DateTime.UtcNow.ToString("o"))
                    + "\",\"actor\":\"" + Escape(actor)
                    + "\",\"model\":\"" + Escape(model)
                    + "\",\"endpoint\":\"" + Escape(endpoint)
                    + "\",\"request\":{\"snapshot\":" + snapshotJson
                    + "},\"response\":{\"plan\":" + planJson
                    + ",\"raw\":" + rawJson + "},\"latencyMs\":" + latencyMs
                    + payloadJson
                    + ",\"decisionSource\":\"" + Escape(source) + "\"}";
                string path = ResolvePath();
                string directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(path, line + "\n");
            }
            catch (Exception) { /* telemetry must never interrupt gameplay */ }
        }

        private static string Escape(string value) => (value ?? string.Empty)
            .Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n");
    }

    // Optional scene-level toggle. Add this component to a root and disable
    // the checkbox to turn off logging without changing AI code.
    public sealed class DecisionLoggerSettings : MonoBehaviour
    {
        [SerializeField] private bool enabledInScene = true;

        private void Awake() => DecisionLogger.Enabled = enabledInScene;
    }
}
