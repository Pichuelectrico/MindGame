using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>Displays one combatant's current health as a numeric legacy uGUI label.</summary>
    public sealed class HealthHUD : MonoBehaviour
    {
        [SerializeField] private Text label;
        [SerializeField] private Game.Combat.HealthSystem health;
        [SerializeField] private string labelPrefix;

        private bool subscribed;

        private void OnEnable()
        {
            if (label == null) label = GetComponent<Text>();
            if (health == null) health = GetComponentInParent<Game.Combat.HealthSystem>();
            if (health != null && !subscribed)
            {
                health.HealthChanged += OnHealthChanged;
                health.Died += OnDied;
                subscribed = true;
            }
            if (health != null) Render(health);
        }

        private void OnDisable()
        {
            if (health != null && subscribed)
            {
                health.HealthChanged -= OnHealthChanged;
                health.Died -= OnDied;
                subscribed = false;
            }
        }

        private void OnHealthChanged(Game.Combat.HealthSystem changed) { Render(changed); }

        private void OnDied(Game.Combat.HealthSystem dead) { Render(dead); }

        private void Render(Game.Combat.HealthSystem source)
        {
            if (label == null || source == null) return;
            int value = Mathf.Max(0, source.CurrentHealth);
            label.text = labelPrefix + (value == 0 ? "0 — MUERTO" : value.ToString());
            label.color = value <= 5 ? Color.red : (value <= 6 ? new Color(1f, 0.55f, 0f) : Color.white);
        }
    }
}
