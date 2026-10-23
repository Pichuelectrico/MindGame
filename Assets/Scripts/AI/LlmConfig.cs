using UnityEngine;

namespace Game.AI
{
    // Endpoint config. The real .asset (with the key) lives in a Git-ignored
    // folder (e.g. Assets/Settings/Local/); only placeholders are committed.
    [CreateAssetMenu(fileName = "LlmConfig", menuName = "MindGame/LLM Config")]
    public sealed class LlmConfig : ScriptableObject
    {
        [SerializeField] private string endpointUrl = "https://example.com/v1/chat";
        [SerializeField] private string apiKey = "PLACEHOLDER_SET_IN_LOCAL_ASSET";
        [SerializeField] private string modelName = "mindgame-boss-1";
        [SerializeField] private float timeoutSeconds = 12f;
        [SerializeField] private int maxRetries = 2;
        [SerializeField] private int retryBackoffMs = 500;

        public string EndpointUrl => endpointUrl;
        public string ApiKey => apiKey;
        public string ModelName => modelName;
        public float TimeoutSeconds => timeoutSeconds;
        public int MaxRetries => maxRetries;
        public int RetryBackoffMs => retryBackoffMs;
    }
}
