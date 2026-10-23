using System;
using UnityEngine;

namespace Game.Combat
{
    public sealed class HealthSystem : MonoBehaviour
    {
        [SerializeField] private int maxHealth = 10;
        [SerializeField] private int currentHealth = 10;

        public int MaxHealth => maxHealth;
        public int CurrentHealth => currentHealth;
        public bool IsDead => currentHealth <= 0;

        // Consecutive hits for the knockdown system: resets after 4s without hits.
        public int HitStreak => Time.time - lastHitTime > StreakWindow ? 0 : hitStreak;
        public const float StreakWindow = 4f;

        // Set by the attacker's CombatDirector before ApplyDamage; read+cleared by
        // the victim's director to pick stagger vs knockdown. Thrown swords count.
        public bool LastHitHeavy { get; set; }

        // [F42] Set by attacker when the hit comes from a thrown sword (ThrowWeapon).
        // Allows the boss to be knocked down ONLY by sword throws.
        public bool LastHitWasThrow { get; set; }

        public event Action<HealthSystem> HealthChanged;
        public event Action<HealthSystem> Died;

        private int hitStreak;
        private float lastHitTime = -100f;

        // Transient invulnerability (e.g. roll i-frames)
        public bool IsInvulnerable => Time.time < invulnerableUntilTime;
        private float invulnerableUntilTime = -100f;

        public void TriggerInvulnerability(float duration)
        {
            invulnerableUntilTime = Mathf.Max(invulnerableUntilTime, Time.time + duration);
        }

        // Runtime init for spawned fighters (grunts): sets max and refills.
        public void SetMaxHealth(int value)
        {
            maxHealth = Mathf.Max(1, value);
            currentHealth = maxHealth;
            hitStreak = 0;
            HealthChanged?.Invoke(this);
        }

        private void Start()
        {
            currentHealth = Mathf.Clamp(currentHealth <= 0 ? maxHealth : currentHealth, 0, maxHealth);
            HealthChanged?.Invoke(this);
        }

        public void ApplyDamage(int amount)
        {
            if (IsDead || amount <= 0 || IsInvulnerable) return;
            hitStreak = Time.time - lastHitTime > StreakWindow ? 1 : hitStreak + 1;
            lastHitTime = Time.time;
            currentHealth = Mathf.Clamp(currentHealth - amount, 0, maxHealth);
            HealthChanged?.Invoke(this);
            if (IsDead) Died?.Invoke(this);
        }

        public void Restore(int amount)
        {
            if (IsDead || amount <= 0) return;
            int next = Mathf.Clamp(currentHealth + amount, 0, maxHealth);
            if (next == currentHealth) return;
            currentHealth = next;
            HealthChanged?.Invoke(this);
        }
    }
}
