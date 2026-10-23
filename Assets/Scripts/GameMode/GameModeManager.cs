using System;
using Game.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.GameMode
{
    public enum GameMode
    {
        Commands,
        Manual
    }

    [DisallowMultipleComponent]
    public sealed class GameModeManager : MonoBehaviour
    {
        public const string ManualHint = "WASD mover · Q esquiva · R rodar · Click Der golpe · J super punch · L lanzar espada · Tab comandos";

        [SerializeField] private GameMode currentMode = GameMode.Commands;
        [SerializeField] private CommandInputUI commandUI;
        [SerializeField] private Button toggleButton;
        [SerializeField] private Text toggleButtonText;

        public GameMode CurrentMode => currentMode;

        public event Action<GameMode> OnModeChanged;

        private void Awake()
        {
            if (commandUI == null) commandUI = FindObjectOfType<CommandInputUI>();
            if (toggleButton == null) toggleButton = GetComponentInChildren<Button>();
            if (toggleButtonText == null && toggleButton != null)
                toggleButtonText = toggleButton.GetComponentInChildren<Text>();
        }

        private void OnEnable()
        {
            if (toggleButton != null)
                toggleButton.onClick.AddListener(ToggleMode);
        }

        private void OnDisable()
        {
            if (toggleButton != null)
                toggleButton.onClick.RemoveListener(ToggleMode);
        }

        private void Start()
        {
            ApplyMode(currentMode);
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.tabKey.wasPressedThisFrame)
            {
                ToggleMode();
            }
        }

        public void ToggleMode()
        {
            SetMode(currentMode == GameMode.Commands ? GameMode.Manual : GameMode.Commands);
        }

        public void SetMode(GameMode newMode)
        {
            currentMode = newMode;
            ApplyMode(currentMode);
            OnModeChanged?.Invoke(currentMode);
        }

        private void ApplyMode(GameMode mode)
        {
            if (mode == GameMode.Manual)
            {
                if (commandUI != null)
                {
                    commandUI.SetInputInteractable(false);
                    commandUI.SetFeedback(ManualHint, true);
                }
                if (toggleButtonText != null)
                    toggleButtonText.text = "Modo: Manual [Tab]";
            }
            else
            {
                if (commandUI != null)
                {
                    commandUI.SetInputInteractable(true);
                    commandUI.SetFeedback(CommandInputUI.DefaultPlaceholder, true);
                    commandUI.FocusInput();
                }
                if (toggleButtonText != null)
                    toggleButtonText.text = "Modo: Comandos [Tab]";
            }
        }

        public void Bind(CommandInputUI ui, Button button, Text buttonText)
        {
            commandUI = ui;
            toggleButton = button;
            toggleButtonText = buttonText;
            ApplyMode(currentMode);
        }
    }
}
