using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Feedback display only: the card wheel is now the sole instruction input.
    // Text input was removed (F5-hotfix); the parser lives on as pure code.
    // SetFeedback + OnCommandSubmitted are kept; input members are obsolete
    // no-ops retained only so older scenes/wirings still compile.
    [DisallowMultipleComponent]
    public sealed class CommandInputUI : MonoBehaviour
    {
        [SerializeField] private InputField inputField;
        [SerializeField] private Button submitButton;
        [SerializeField] private Text titleText;
        [SerializeField] private Text feedbackText;
        [SerializeField] private float instructionsDuration = 30f;
        [SerializeField] private float fadeDuration = 1.5f;
        [SerializeField] private bool autoHideInstructions = true;

        private float gameTime = 0f;
        private bool instructionsHidden = false;

        public bool InstructionsHidden => instructionsHidden;
        public float InstructionsDuration { get => instructionsDuration; set => instructionsDuration = value; }
        public float FadeDuration { get => fadeDuration; set => fadeDuration = value; }
        public bool AutoHideInstructions { get => autoHideInstructions; set => autoHideInstructions = value; }
        public Text TitleText => titleText;
        public Text FeedbackText => feedbackText;

        public void SetTitleText(Text title) => titleText = title;
        public void SetFeedbackText(Text feedback) => feedbackText = feedback;

        [Obsolete("Text input removed; the action wheel is the only input.")]
        public const string DefaultPlaceholder = "Usa la rueda de acciones [R]";

        public event Action<string> OnCommandSubmitted;

        public string CurrentText => inputField != null ? inputField.text : string.Empty;

        [Obsolete("Text input removed; always false.")]
        public bool IsFocused => false;

        private static bool eventSystemRepairLogged;

        private void Awake()
        {
            EnsureInputSystemEventModule();
            CacheReferences();
        }

        private void Start()
        {
            if (feedbackText != null && string.IsNullOrEmpty(feedbackText.text))
                SetFeedback("Pulsa R o el botón para abrir la rueda de acciones", true);
        }

        private void OnEnable()
        {
            if (submitButton != null)
                submitButton.onClick.AddListener(SubmitCurrent);
        }

        private void OnDisable()
        {
            if (submitButton != null)
                submitButton.onClick.RemoveListener(SubmitCurrent);
        }

        private void Update()
        {
            AdvanceGameTime(Time.deltaTime);

            if (inputField == null || !inputField.isFocused) return;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame))
                SubmitCurrent();
        }

        public void AdvanceGameTime(float deltaTime)
        {
            if (!autoHideInstructions || instructionsHidden) return;
            gameTime += deltaTime;
            if (gameTime >= instructionsDuration)
            {
                HideInstructions();
            }
            else if (fadeDuration > 0f && gameTime >= instructionsDuration - fadeDuration)
            {
                float t = (instructionsDuration - gameTime) / fadeDuration;
                if (feedbackText != null)
                {
                    var c = feedbackText.color;
                    c.a = Mathf.Clamp01(t);
                    feedbackText.color = c;
                }
                if (titleText != null)
                {
                    var c = titleText.color;
                    c.a = Mathf.Clamp01(t);
                    titleText.color = c;
                }
            }
        }

        public void HideInstructions()
        {
            instructionsHidden = true;
            if (feedbackText != null)
                feedbackText.gameObject.SetActive(false);
            if (titleText != null)
                titleText.gameObject.SetActive(false);
        }

        public void ShowInstructions()
        {
            instructionsHidden = false;
            gameTime = 0f;
            if (feedbackText != null)
            {
                var c = feedbackText.color;
                c.a = 1f;
                feedbackText.color = c;
                feedbackText.gameObject.SetActive(true);
            }
            if (titleText != null)
            {
                var c = titleText.color;
                c.a = 1f;
                titleText.color = c;
                titleText.gameObject.SetActive(true);
            }
        }

        [Obsolete("Text input removed; kept for wiring compatibility.")]
        public void SubmitCurrent()
        {
            if (inputField == null) return;
            string text = inputField.text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return;
            OnCommandSubmitted?.Invoke(text);
        }

        public void SetFeedback(string message, bool accepted)
        {
            if (feedbackText == null) return;
            feedbackText.text = message ?? string.Empty;
            feedbackText.color = accepted ? Color.green : Color.red;
            if (instructionsHidden)
            {
                feedbackText.gameObject.SetActive(false);
            }
        }

        [Obsolete("Text input removed; no-op.")]
        public void ClearInput()
        {
        }

        [Obsolete("Text input removed; no-op.")]
        public void SetInputInteractable(bool interactable)
        {
        }

        [Obsolete("Text input removed; no-op.")]
        public void SetPlaceholder(string placeholder)
        {
        }

        [Obsolete("Text input removed; no-op.")]
        public void FocusInput()
        {
        }

        // Runtime shield: a stale EventSystem with the legacy StandaloneInputModule
        // throws every frame under Active Input Handling = Input System. Repair in
        // place so Play Mode heals itself without menus or scene re-saves.
        private static void EnsureInputSystemEventModule()
        {
            var current = UnityEngine.EventSystems.EventSystem.current;
            if (current == null) return;
            var legacy = current.GetComponent<UnityEngine.EventSystems.StandaloneInputModule>();
            if (legacy == null) return;
            Destroy(legacy);
            if (current.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>() == null)
                current.gameObject.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            if (!eventSystemRepairLogged)
            {
                eventSystemRepairLogged = true;
                Debug.Log("[CommandInputUI] Replaced legacy StandaloneInputModule with InputSystemUIInputModule at runtime.");
            }
        }

        private void CacheReferences()
        {
            if (titleText == null)
            {
                var titleGo = transform.Find("Title");
                if (titleGo != null) titleText = titleGo.GetComponent<Text>();
            }
            if (feedbackText == null)
            {
                var feedbackGo = transform.Find("Feedback");
                if (feedbackGo != null) feedbackText = feedbackGo.GetComponent<Text>();
                else
                {
                    var texts = GetComponentsInChildren<Text>(true);
                    feedbackText = texts.Length > 0 ? texts[texts.Length - 1] : null;
                }
            }
        }
    }
}
