using Game.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    // [F37] Full-screen game over overlay for Victory / Defeat.
    // Freezes player gameplay, shows clear announcement, and provides
    // Enter key & clickable button restart.
    [DisallowMultipleComponent]
    public sealed class GameOverUI : MonoBehaviour
    {
        [Header("UI Elements")]
        [SerializeField] private GameObject overlayPanel;
        [SerializeField] private Text titleText;
        [SerializeField] private Text messageText;
        [SerializeField] private Text hintText;
        [SerializeField] private Button restartButton;

        [Header("Manager")]
        [SerializeField] private GameOverManager manager;

        public bool IsShowing => overlayPanel != null && overlayPanel.activeSelf;
        public string Title => titleText != null ? titleText.text : string.Empty;
        public string Message => messageText != null ? messageText.text : string.Empty;

        private void Awake()
        {
            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(OnRestartClicked);
                restartButton.onClick.AddListener(OnRestartClicked);
            }

            if (manager == null)
                manager = FindFirstObjectByType<GameOverManager>();

            Hide();
        }

        private void OnEnable()
        {
            if (manager != null)
                manager.GameOver += OnGameOverResult;
        }

        private void OnDisable()
        {
            if (manager != null)
                manager.GameOver -= OnGameOverResult;
        }

        private void Update()
        {
            if (!IsShowing) return;

            // Allow Enter / NumpadEnter / R to restart the match
            var kb = Keyboard.current;
            if (kb != null && (kb.enterKey.wasPressedThisFrame ||
                               kb.numpadEnterKey.wasPressedThisFrame ||
                               kb.rKey.wasPressedThisFrame))
            {
                OnRestartClicked();
            }
        }

        private void OnGameOverResult(GameResult result)
        {
            if (result == GameResult.Victory)
                ShowVictory();
            else if (result == GameResult.Defeat)
                ShowDefeat();
        }

        public void ShowVictory()
        {
            if (overlayPanel != null)
                overlayPanel.SetActive(true);

            if (titleText != null)
            {
                titleText.text = "¡VICTORIA!";
                titleText.color = new Color(0.2f, 0.95f, 0.35f, 1f); // Vibrant Victory Green
                titleText.gameObject.SetActive(true);
            }

            if (messageText != null)
            {
                messageText.text = "¡HAS MATADO AL MUTANTE!";
                messageText.gameObject.SetActive(true);
            }

            if (hintText != null)
            {
                hintText.text = "Pulsa ENTER o haz clic en Reintentar";
                hintText.gameObject.SetActive(true);
            }

            ReleaseCursor();
        }

        public void ShowDefeat()
        {
            if (overlayPanel != null)
                overlayPanel.SetActive(true);

            if (titleText != null)
            {
                titleText.text = "¡DERROTA!";
                titleText.color = new Color(1f, 0.25f, 0.25f, 1f); // Defeat Red
                titleText.gameObject.SetActive(true);
            }

            if (messageText != null)
            {
                messageText.text = "HAS CAÍDO EN COMBATE";
                messageText.gameObject.SetActive(true);
            }

            if (hintText != null)
            {
                hintText.text = "Pulsa ENTER o haz clic en Reintentar";
                hintText.gameObject.SetActive(true);
            }

            ReleaseCursor();
        }

        public void Hide()
        {
            if (overlayPanel != null)
                overlayPanel.SetActive(false);
        }

        private void ReleaseCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void OnRestartClicked()
        {
            if (manager != null)
            {
                manager.Restart();
            }
            else
            {
                var scene = SceneManager.GetActiveScene();
                if (scene.IsValid())
                    SceneManager.LoadScene(scene.name);
            }
        }
    }
}
