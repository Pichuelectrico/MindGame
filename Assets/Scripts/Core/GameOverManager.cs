using System;
using Game.Combat;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Game.Core
{
    public enum GameResult { None, Victory, Defeat }

    // [F37] Game loop closure: first death wins. Victory = boss dead, Defeat = player dead.
    // When game ends:
    // 1. Spawner freezes (spawner.enabled = false).
    // 2. Gameplay freezes via time.EnterPlanning() (stops controllers & player input).
    // 3. Cursor unlocked for UI interaction.
    // 4. GameOverUI displays full-screen victory or defeat banner.
    // 5. Restart supported via UI button or Enter / R keys.
    [DisallowMultipleComponent]
    public sealed class GameOverManager : MonoBehaviour
    {
        [SerializeField] private HealthSystem bossHealth;
        [SerializeField] private HealthSystem playerHealth;
        [SerializeField] private GruntSpawner spawner;
        [SerializeField] private TimeController time;
        [SerializeField] private Game.UI.GameOverHUD hud;
        [SerializeField] private Game.UI.GameOverUI gameOverUI;

        public GameResult Result { get; private set; } = GameResult.None;
        public GruntSpawner Spawner => spawner;
        public TimeController Time => time;
        public Game.UI.GameOverUI UI => gameOverUI;

        public event Action<GameResult> GameOver;

        private void Awake()
        {
            ResolveMissingReferences();
        }

        private void OnEnable()
        {
            ResolveMissingReferences();
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void ResolveMissingReferences()
        {
            if (bossHealth == null)
            {
                var bossGo = GameObject.Find("Boss_Mutant");
                if (bossGo != null) bossHealth = bossGo.GetComponent<HealthSystem>();
            }
            if (playerHealth == null)
            {
                var playerGo = GameObject.Find("Player_YBot");
                if (playerGo != null) playerHealth = playerGo.GetComponent<HealthSystem>();
            }
            if (spawner == null)
            {
                spawner = FindFirstObjectByType<GruntSpawner>();
            }
            if (time == null)
            {
                time = FindFirstObjectByType<TimeController>();
            }
            if (gameOverUI == null)
            {
                gameOverUI = FindFirstObjectByType<Game.UI.GameOverUI>();
            }
            if (hud == null)
            {
                hud = FindFirstObjectByType<Game.UI.GameOverHUD>();
            }
        }

        // EditMode-friendly wiring (OnEnable doesn't run in tests).
        public void Bind(HealthSystem boss, HealthSystem player, GruntSpawner sp, Game.UI.GameOverHUD h)
        {
            Bind(boss, player, sp, h, null, null);
        }

        public void Bind(HealthSystem boss, HealthSystem player, GruntSpawner sp, Game.UI.GameOverHUD h, TimeController t, Game.UI.GameOverUI ui)
        {
            Unsubscribe();
            bossHealth = boss;
            playerHealth = player;
            spawner = sp;
            hud = h;
            time = t;
            gameOverUI = ui;
            Subscribe();
        }

        private void Subscribe()
        {
            if (bossHealth != null)
            {
                bossHealth.Died -= OnBossDied;
                bossHealth.Died += OnBossDied;
            }
            if (playerHealth != null)
            {
                playerHealth.Died -= OnPlayerDied;
                playerHealth.Died += OnPlayerDied;
            }
        }

        private void Unsubscribe()
        {
            if (bossHealth != null) bossHealth.Died -= OnBossDied;
            if (playerHealth != null) playerHealth.Died -= OnPlayerDied;
        }

        private void Update()
        {
            if (Result == GameResult.None) return;
            var kb = Keyboard.current;
            if (kb != null && (kb.rKey.wasPressedThisFrame ||
                               kb.enterKey.wasPressedThisFrame ||
                               kb.numpadEnterKey.wasPressedThisFrame))
            {
                Restart();
            }
        }

        public void Restart()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.IsValid()) SceneManager.LoadScene(scene.name);
        }

        private void OnBossDied(HealthSystem h)
        {
            if (Result != GameResult.None) return;
            Result = GameResult.Victory;

            // 1. Freeze spawner
            if (spawner != null) spawner.enabled = false;

            // 2. Freeze gameplay controllers and player input
            if (time != null) time.EnterPlanning();

            // 3. Unlock cursor for UI
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // 4. Show UI
            if (gameOverUI != null) gameOverUI.ShowVictory();
            if (hud != null) hud.ShowVictory();

            Debug.Log("[GameOver] ¡VICTORIA! Has derrotado al Mutante. Pulsa ENTER o Reintentar.");
            GameOver?.Invoke(Result);
        }

        private void OnPlayerDied(HealthSystem h)
        {
            if (Result != GameResult.None) return;
            Result = GameResult.Defeat;

            // 1. Freeze spawner
            if (spawner != null) spawner.enabled = false;

            // 2. Freeze gameplay controllers and player input
            if (time != null) time.EnterPlanning();

            // 3. Unlock cursor for UI
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // 4. Show UI
            if (gameOverUI != null) gameOverUI.ShowDefeat();
            if (hud != null) hud.ShowDefeat();

            Debug.Log("[GameOver] HAS CAÍDO EN COMBATE. Pulsa ENTER o Reintentar.");
            GameOver?.Invoke(Result);
        }
    }
}
