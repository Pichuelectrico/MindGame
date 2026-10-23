using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.AI
{
    // Explicit result: never throws to the caller. Null/!Success means
    // "use the heuristic BotBrain fallback".
    public sealed class LlmResult
    {
        public bool Success { get; private set; }
        public LlmPlanResponse Plan { get; private set; }
        public string Error { get; private set; }
        // [F14.2] Raw transport body when one arrived, so DecisionLogger can
        // record the model's literal output (also on invalid-plan failures).
        // Null on transport-level failures where no body exists.
        public string RawResponse { get; private set; }

        public static LlmResult Ok(LlmPlanResponse plan, string raw = null) =>
            new LlmResult { Success = true, Plan = plan, RawResponse = raw };

        public static LlmResult Fail(string error, string raw = null) =>
            new LlmResult { Success = false, Error = error ?? "unknown error", RawResponse = raw };
    }

    // Injectable transport so EditMode tests run without real network.
    public interface ILlmTransport
    {
        Task<TransportResponse> PostAsync(string url, string jsonBody, string apiKey, float timeoutSeconds);
    }

    public sealed class TransportResponse
    {
        public bool Ok;
        public string Body;
        public string Error;
    }

    // Production transport over UnityWebRequest. Awaits without blocking main thread.
    public sealed class UnityWebRequestTransport : ILlmTransport
    {
        public async Task<TransportResponse> PostAsync(string url, string jsonBody, string apiKey, float timeoutSeconds)
        {
            byte[] payload = Encoding.UTF8.GetBytes(jsonBody ?? string.Empty);
            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(apiKey))
                    req.SetRequestHeader("Authorization", "Bearer " + apiKey);
                req.timeout = Mathf.Max(1, Mathf.CeilToInt(timeoutSeconds));

                var op = req.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

#if UNITY_2020_2_OR_NEWER
                if (req.result != UnityWebRequest.Result.Success)
                    return new TransportResponse { Ok = false, Error = req.error };
#else
                if (req.isNetworkError || req.isHttpError)
                    return new TransportResponse { Ok = false, Error = req.error };
#endif
                return new TransportResponse { Ok = true, Body = req.downloadHandler.text };
            }
        }
    }

    // Pure client (no MonoBehaviour): builds payload, retries with backoff, parses plan.
    public sealed class LlmClient
    {
        private readonly LlmConfig config;
        private readonly ILlmTransport transport;

        public LlmClient(LlmConfig config, ILlmTransport transport = null)
        {
            this.config = config;
            this.transport = transport ?? new UnityWebRequestTransport();
        }

        public async Task<LlmResult> SendCombatStateAsync(CombatStateSnapshot snapshot, string systemPrompt)
        {
            try
            {
                if (config == null)
                    return LlmResult.Fail("missing LlmConfig");
                if (snapshot == null)
                    return LlmResult.Fail("null snapshot");

                string body = BuildPayload(snapshot, systemPrompt);
                int attempts = 1 + Mathf.Max(0, config.MaxRetries);
                string lastError = "no attempts";
                string lastRawBody = null;

                for (int i = 0; i < attempts; i++)
                {
                    if (i > 0)
                        await Task.Delay(config.RetryBackoffMs * i);

                    TransportResponse resp;
                    try
                    {
                        resp = await transport.PostAsync(
                            config.EndpointUrl, body, config.ApiKey, config.TimeoutSeconds);
                    }
                    catch (Exception ex)
                    {
                        lastError = "transport exception: " + ex.Message;
                        continue;
                    }

                    if (resp == null || !resp.Ok)
                    {
                        lastError = resp != null ? resp.Error : "null transport response";
                        if (resp != null && !string.IsNullOrEmpty(resp.Body))
                            lastRawBody = resp.Body;
                        continue;
                    }
                    lastRawBody = resp.Body;

                    var plan = LlmPlanResponse.FromJson(ExtractPlanJson(resp.Body));
                    if (!plan.IsValid())
                    {
                        lastError = "invalid plan JSON (must be 1..6 actions with non-empty type)";
                        continue;
                    }
                    return LlmResult.Ok(plan, resp.Body);
                }
                return LlmResult.Fail(lastError, lastRawBody);
            }
            catch (Exception ex)
            {
                return LlmResult.Fail("unexpected: " + ex.Message);
            }
        }

        // [F52] Minimal one-word strategy call: same retry/backoff/timeout
        // discipline as SendCombatStateAsync but NO plan parsing — the model
        // answers ONE word (Distance|Fighter|Normal) and the raw content
        // passes through to LlmBrain.ParsePersona. Never throws; null on any
        // failure means "Normal" for the caller.
        public async Task<string> SendRawAsync(string systemPrompt, string userContent)
        {
            try
            {
                if (config == null) return null;
                string body = BuildPayloadRaw(systemPrompt, userContent);
                int attempts = 1 + Mathf.Max(0, config.MaxRetries);
                for (int i = 0; i < attempts; i++)
                {
                    if (i > 0)
                        await Task.Delay(config.RetryBackoffMs * i);
                    TransportResponse resp;
                    try
                    {
                        resp = await transport.PostAsync(
                            config.EndpointUrl, body, config.ApiKey, config.TimeoutSeconds);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (resp == null || !resp.Ok) continue;
                    string content = ExtractContent(resp.Body);
                    if (!string.IsNullOrWhiteSpace(content)) return content;
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // [F52] OpenAI-compatible chat payload for a free-form strategy
        // prompt (system instructions + short user context lines).
        public string BuildPayloadRaw(string systemPrompt, string userContent)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"model\":\"").Append(Escape(config != null ? config.ModelName : "?")).Append("\",");
            sb.Append("\"messages\":[{\"role\":\"system\",\"content\":\"")
                .Append(Escape(systemPrompt ?? string.Empty)).Append("\"},{\"role\":\"user\",\"content\":\"")
                .Append(Escape(userContent ?? string.Empty)).Append("\"}]}");
            return sb.ToString();
        }

        // [F52] Chat-envelope content extraction (null when absent/invalid).
        internal static string ExtractContent(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                var envelope = JsonUtility.FromJson<ChatEnvelope>(body);
                return envelope != null && envelope.choices != null && envelope.choices.Length > 0
                    && envelope.choices[0] != null && envelope.choices[0].message != null
                    ? envelope.choices[0].message.content
                    : null;
            }
            catch (Exception) { return null; }
        }

        // OpenAI-compatible chat payload consumed by Ollama /v1/chat/completions.
        public string BuildPayload(CombatStateSnapshot snapshot, string systemPrompt)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"model\":\"").Append(Escape(config != null ? config.ModelName : "?")).Append("\",");
            sb.Append("\"messages\":[{\"role\":\"system\",\"content\":\"")
                .Append(Escape(systemPrompt ?? string.Empty)).Append("\"},{\"role\":\"user\",\"content\":\"")
                .Append(Escape(CombatStateSerializer.ToJson(snapshot))).Append("\"}]}" );
            return sb.ToString();
        }

        // Accepts either a raw plan {"actions":...} or a chat-style envelope
        // {"choices":[{"message":{"content":"{...}"}}]}; falls back to raw body.
        internal static string ExtractPlanJson(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;
            try
            {
                var envelope = JsonUtility.FromJson<ChatEnvelope>(body);
                var content = envelope != null && envelope.choices != null && envelope.choices.Length > 0
                    && envelope.choices[0] != null && envelope.choices[0].message != null
                    ? envelope.choices[0].message.content
                    : null;
                if (!string.IsNullOrEmpty(content) && content.IndexOf("\"actions\"") >= 0)
                    return content;
            }
            catch (Exception) { /* fall through to raw body */ }
            return body;
        }

        // [F38-fix][lead] Escape control chars too: the F35 system prompt is multi-line, and a raw
        // \n inside a JSON string makes Ollama reject the whole request with 400
        // ("invalid character '\n' in string literal") → every boss decision fell back to BotBrain.
        private static string Escape(string s) =>
            (s ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");

        [Serializable]
        private sealed class ChatEnvelope
        {
            public ChatChoice[] choices;
        }

        [Serializable]
        private sealed class ChatChoice
        {
            public ChatMessage message;
        }

        [Serializable]
        private sealed class ChatMessage
        {
            public string content;
        }
    }
}
