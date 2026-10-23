using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// F6: shows how many NPCs the player has defeated ("NPCs: n/5"). When the
    /// quota is reached it flashes "¡EL MUTANTE ENTRA!" while the boss gate opens.
    /// Subscribes to GruntSpawner.OnGruntKilled — the event carries the RUNNING
    /// total of kills (not a delta). Same subscription hygiene as HealthHUD:
    /// subscribe in OnEnable, unsubscribe in OnDisable, null-safe throughout.
    /// </summary>
    public sealed class KillCounterHUD : MonoBehaviour
    {
        [SerializeField] private Text label;
        [SerializeField] private Game.Combat.GruntSpawner spawner;
        [SerializeField] private float announceSeconds = 2f;

        private float announceTimer;
        private bool announcing;
        private bool announcedBossEntry;
        private bool subscribed;

        private void OnEnable()
        {
            if (label == null) label = GetComponent<Text>();
            if (spawner == null) spawner = FindFirstObjectByType<Game.Combat.GruntSpawner>();
            if (spawner != null && !subscribed)
            {
                spawner.OnGruntKilled += OnGruntKilled;
                subscribed = true;
            }
            Render(spawner != null ? spawner.KillCount : 0);
        }

        private void OnDisable()
        {
            if (spawner != null && subscribed)
            {
                spawner.OnGruntKilled -= OnGruntKilled;
                subscribed = false;
            }
        }

        private void Update()
        {
            if (!announcing) return;
            announceTimer -= Time.unscaledDeltaTime;
            if (announceTimer <= 0f)
            {
                announcing = false;
                Render(spawner != null ? spawner.KillCount : 0);
                return;
            }
            // Yellow ↔ red pulse while the notice is up (unscaled: survives pause).
            float t = Mathf.PingPong(Time.unscaledTime * 4f, 1f);
            if (label != null)
                label.color = Color.Lerp(Color.yellow, new Color(1f, 0.2f, 0.2f), t);
        }

        private void OnGruntKilled(int total)
        {
            if (!announcedBossEntry && spawner != null && total >= spawner.KillsToWakeBoss)
            {
                announcedBossEntry = true;
                announcing = true;
                announceTimer = announceSeconds;
                if (label != null)
                {
                    label.text = "¡EL MUTANTE ENTRA!";
                    label.color = Color.yellow;
                }
                return;
            }
            Render(total);
        }

        private void Render(int total)
        {
            if (label == null || announcing) return;
            int goal = spawner != null ? spawner.KillsToWakeBoss : 5;
            label.text = "NPCs: " + total + "/" + goal;
            label.color = Color.white;
        }
    }
}
