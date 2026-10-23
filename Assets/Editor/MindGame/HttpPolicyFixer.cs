using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace MindGame.EditorTools
{
    /// <summary>
    /// Unity 6 blocks plain-HTTP UnityWebRequest calls by default ("Non-secure network
    /// connections disabled in Player Settings" / "Insecure connection not allowed"),
    /// even inside the Editor — which silently killed the boss LLM calls to the LAN
    /// Ollama server (latency 0ms, instant fallback to BotBrain).
    ///
    /// Unity 6 exposes the setting as PlayerSettings.insecureHttpOption
    /// (InsecureHttpOption: NotAllowed=0, AllowedInBuild=1, AlwaysAllowed=2).
    /// Serialized in ProjectSettings.asset as "insecureHttpOption".
    /// </summary>
    public static class HttpPolicyFixer
    {
        [MenuItem("MindGame/Setup/Allow HTTP Downloads (LLM LAN)")]
        public static void AllowHttpDownloads()
        {
            var prop = typeof(PlayerSettings).GetProperty("insecureHttpOption",
                BindingFlags.Public | BindingFlags.Static);
            if (prop == null)
            {
                Debug.LogError("[HttpPolicyFixer] PlayerSettings.insecureHttpOption NOT FOUND — set it manually: Project Settings > Player > Other Settings > Allow downloads over HTTP.");
                return;
            }

            var enumType = prop.PropertyType;
            var names = Enum.GetNames(enumType);
            Debug.Log("[HttpPolicyFixer] " + enumType.FullName + " options: " + string.Join(", ", names) +
                " | current=" + prop.GetValue(null, null));

            object target = null;
            string targetName = null;
            foreach (var name in names)
            {
                if (name.ToLower().Contains("always"))
                {
                    target = Enum.Parse(enumType, name);
                    targetName = name;
                    break;
                }
            }
            if (target == null)
            {
                // Fallback: numeric 2 (AlwaysAllowed in every known Unity version).
                target = Enum.ToObject(enumType, 2);
                targetName = "2 (AlwaysAllowed)";
            }

            prop.SetValue(null, target, null);
            Debug.Log("[HttpPolicyFixer] insecureHttpOption -> " + targetName +
                " | readback=" + prop.GetValue(null, null) + " (persisted to ProjectSettings.asset)");
        }

        [MenuItem("MindGame/Diagnostics/Test LLM Chat POST (real transport)")]
        public static void TestChatPost()
        {
            var cfg = AssetDatabase.LoadAssetAtPath<Game.AI.LlmConfig>("Assets/Config/LlmConfig.asset");
            string url = "http://127.0.0.1:11434/v1/chat/completions";
            string model = "qwen2.5:7b";
            if (cfg != null)
            {
                if (!string.IsNullOrEmpty(cfg.EndpointUrl)) url = cfg.EndpointUrl;
                if (!string.IsNullOrEmpty(cfg.ModelName)) model = cfg.ModelName;
            }

            string body = "{\"model\":\"" + model + "\",\"messages\":[" +
                "{\"role\":\"system\",\"content\":\"Reply with ONLY {\\\"actions\\\":[{\\\"type\\\":\\\"Run\\\"}]}\"}," +
                "{\"role\":\"user\",\"content\":\"{\\\"phase\\\":\\\"Running\\\"}\"}]}";
            Debug.Log("[HttpPolicyFixer] POST " + url + " (model=" + model + ", body " + body.Length + " bytes)");

            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer test");
            req.timeout = 12;
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var t = req.downloadHandler.text ?? "";
                    Debug.Log("[HttpPolicyFixer] POST OK " + req.responseCode + " — chat transport WORKS. Body head: " +
                        t.Substring(0, Math.Min(300, t.Length)));
                }
                else
                {
                    string respBody = req.downloadHandler != null && req.downloadHandler.text != null
                        ? req.downloadHandler.text.Substring(0, Math.Min(200, req.downloadHandler.text.Length))
                        : "null";
                    Debug.LogError("[HttpPolicyFixer] POST FAILED: " + req.error +
                        " (result=" + req.result + ", code=" + req.responseCode + ", body=" + respBody + ")");
                }
                req.Dispose();
            };
        }

        [MenuItem("MindGame/Diagnostics/Test LLM Endpoint (HTTP policy)")]
        public static void TestEndpoint()
        {
            string url = "http://127.0.0.1:11434/v1/models";
            var cfg = AssetDatabase.LoadAssetAtPath<Game.AI.LlmConfig>("Assets/Config/LlmConfig.asset");
            if (cfg != null && !string.IsNullOrEmpty(cfg.EndpointUrl))
            {
                var url2 = cfg.EndpointUrl;
                int q = url2.IndexOf("/chat/completions", StringComparison.Ordinal);
                if (q > 0) url2 = url2.Substring(0, q) + "/models";
                url = url2;
            }
            Debug.Log("[HttpPolicyFixer] GET " + url);
            var req = UnityWebRequest.Get(url);
            req.timeout = 10;
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var body = req.downloadHandler.text ?? "";
                    Debug.Log("[HttpPolicyFixer] HTTP OK " + req.responseCode + " — policy fix WORKS. Body: " +
                        body.Substring(0, Math.Min(220, body.Length)));
                }
                else
                {
                    Debug.LogError("[HttpPolicyFixer] FAILED: " + req.error + " (result=" + req.result + ")");
                }
                req.Dispose();
            };
        }
    }
}
