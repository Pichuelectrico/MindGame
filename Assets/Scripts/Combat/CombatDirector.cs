using Game.Execution;
using UnityEngine;

namespace Game.Combat
{
    // Per-fighter glue: turns the executor's impact moment into damage, and
    // damage taken into HitReaction / Death plans. Death is terminal and visible.
    [DisallowMultipleComponent]
    public sealed class CombatDirector : MonoBehaviour
    {
        private const string KnockdownPlanJson =
            "{\"actions\":[{\"type\":\"Knockdown\"},{\"type\":\"StandUp\"}]}";

        private static readonly string[] AttackIds =
        {
            "AttackSword", "Punch", "ThrowWeapon", "BossHeavyAttack",
            "AttackKick", "AttackCombo", "MutantPunch", "AttackSlide",
            "SuperPunch"
        };

        [SerializeField] private ActionExecutor executor;
        [SerializeField] private HealthSystem health;
        [SerializeField] private Game.Core.TimeController time;
        [SerializeField] private Game.GameMode.GameModeManager modeManager;

        // [F7] Damage feel: per-hit damage by action weight (isHeavy).
        // Defaults keep legacy behavior (everything = 1); the boss wire
        // overrides them for a real fight.
        [SerializeField] private int lightHitDamage = 1;
        [SerializeField] private int heavyHitDamage = 1;

        private int lastHealth = -1;
        private Vector3 lastPosition;
        private float lastMoveTime = -10f;

        // Runtime wiring (spawner): AddComponent runs OnEnable with nulls, so
        // re-subscribe explicitly after assigning.
        public void Bind(ActionExecutor e, HealthSystem h, Game.Core.TimeController t)
        {
            OnDisable();
            executor = e;
            health = h;
            time = t;
            if (health != null) lastHealth = health.CurrentHealth;
            lastPosition = transform.position;
            OnEnable();
        }

        public void BindModeManager(Game.GameMode.GameModeManager m) => modeManager = m;

        private void Awake()
        {
            // [F24 hardening] Every combatant with a CombatDirector (player, boss,
            // grunts) must collide with arena walls/pillars even if the scene's
            // serialized CharacterCollisionGate was lost to a hand-edited save.
            // Idempotent — skips when a gate already exists.
            CharacterCollisionGate.EnsureOn(gameObject);
        }

        private bool IsPlayerInManualMode()
        {
            if (!GruntSpawner.IsPlayer(gameObject) && GetComponent<Game.GameMode.PlayerManualController>() == null)
                return false;

            if (modeManager == null)
                modeManager = GetComponent<Game.GameMode.GameModeManager>() ?? FindFirstObjectByType<Game.GameMode.GameModeManager>();

            return modeManager == null || modeManager.CurrentMode == Game.GameMode.GameMode.Manual;
        }

        private void Start()
        {
            lastPosition = transform.position;
            if (modeManager == null)
                modeManager = GetComponent<Game.GameMode.GameModeManager>() ?? FindFirstObjectByType<Game.GameMode.GameModeManager>();
        }

        private void Update()
        {
            // Moved recently (locomotion or manual WASD)? Don't fight it.
            if ((transform.position - lastPosition).sqrMagnitude > 0.0001f)
            {
                lastPosition = transform.position;
                lastMoveTime = Time.time;
                return;
            }
            lastPosition = transform.position;

            // [F25] In Manual mode (player controls), facing is purely user/WASD-driven:
            // no idle auto-turning toward enemies.
            if (IsPlayerInManualMode()) return;

            // Idle facing: slow turn toward the nearest living enemy while idle,
            // alive, in Running, and stationary (never fights manual control).
            if (executor == null || executor.Current != null) return;
            if (health != null && health.IsDead) return;
            if (time != null && time.Current != Game.Core.CombatPhase.Running) return;
            if (Time.time - lastMoveTime < 0.5f) return;
            var enemy = NearestLivingEnemy(float.MaxValue);
            if (enemy == null) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                Quaternion.LookRotation(Flat(enemy.transform.position - transform.position)),
                120f * Time.deltaTime);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 0.0001f ? Vector3.forward : v.normalized;
        }

        private void OnEnable()
        {
            if (executor != null)
            {
                executor.Health = health;
                executor.ActionImpact += OnActionImpact;
                executor.ActionStarted += OnActionStarted;
            }
            if (health != null)
            {
                lastHealth = health.CurrentHealth;
                health.HealthChanged += OnHealthChanged;
                health.Died += OnDied;
            }
        }

        private void OnDisable()
        {
            if (executor != null)
            {
                executor.ActionImpact -= OnActionImpact;
                executor.ActionStarted -= OnActionStarted;
                if (executor.Health == health) executor.Health = null;
            }
            if (health != null)
            {
                health.HealthChanged -= OnHealthChanged;
                health.Died -= OnDied;
            }
        }

        // Attacks always face the nearest living enemy at Enter in Commands mode and for NPCs.
        // [F25] In Manual mode (player controls), attacks respect the player's current facing
        // with zero aim assist / auto-snap to enemies.
        private void OnActionStarted(ActionInstance instance)
        {
            if (health != null && health.IsDead) return;
            if (IsPlayerInManualMode()) return;
            var def = instance?.Action?.Definition;
            if (def == null || !IsAttackId(def.ActionId)) return;
            var victim = NearestLivingEnemy(float.MaxValue);
            if (victim == null) return;
            transform.rotation = Quaternion.LookRotation(Flat(victim.transform.position - transform.position));
        }

        private void OnActionImpact(ActionInstance instance)
        {
            if (health != null && health.IsDead) return;
            var def = instance?.Action?.Definition;
            if (def == null || !IsAttackId(def.ActionId)) return;
            var victim = NearestLivingEnemy(def.Range);
            if (victim == null) return;
            victim.LastHitHeavy = def.IsHeavy;
            // [F42] Track if this hit is from a thrown sword — only this knocks down the boss.
            victim.LastHitWasThrow = string.Equals(def.ActionId, "ThrowWeapon", System.StringComparison.OrdinalIgnoreCase);
            // [F7] Damage by action weight (defaults = legacy 1/1; boss wire sets 2/4).
            int dmg = def.IsHeavy ? heavyHitDamage : lightHitDamage;
            if (string.Equals(def.ActionId, "SuperPunch", System.StringComparison.OrdinalIgnoreCase))
                dmg = lightHitDamage * 3;
            victim.ApplyDamage(dmg);
            Debug.Log("[CombatDirector] " + name + " hits " + victim.name
                + " with " + def.ActionId + " (" + victim.CurrentHealth + " HP left).");
        }

        private void OnHealthChanged(HealthSystem h)
        {
            if (h.IsDead) { lastHealth = h.CurrentHealth; return; }
            bool damaged = lastHealth >= 0 && h.CurrentHealth < lastHealth;
            lastHealth = h.CurrentHealth;
            if (!damaged || executor == null) return;
            // F8 immediate reactions (F6 queue reverted): interrupt the running
            // plan and play the reaction NOW. Knockdown routing intact.
            bool heavy = h.LastHitHeavy;
            bool wasThrow = h.LastHitWasThrow;
            h.LastHitHeavy = false;
            h.LastHitWasThrow = false;
            // [F42] Boss armor: suppress HitReaction/knockdown unless hit by a thrown sword.
            bool isBoss = GruntSpawner.IsBoss(h.gameObject);
            if (isBoss && !wasThrow)
            {
                // Damage applies (HP reduced) but no stagger/knockdown reaction.
                return;
            }
            executor.StopPlan();
            if (heavy || h.HitStreak >= 5 || wasThrow)
                executor.QueueReactionPlan(KnockdownPlanJson);
            else
                executor.QueueReaction("HitReaction");
        }

        private void OnDied(HealthSystem h)
        {
            lastHealth = 0;
            Debug.Log("[CombatDirector] " + name + " died.");
            if (executor == null) return;
            // Open the death gate so the corpse's own Death plan loads, then play
            // it immediately (interrupting whatever ran).
            executor.Health = null;
            executor.StopPlan();
            executor.QueueReaction("Death");
        }

        public static bool IsAttackId(string actionId)
        {
            foreach (var id in AttackIds)
                if (string.Equals(id, actionId, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private HealthSystem NearestLivingEnemy(float range)
        {
            // Grunts fight ONLY the player (their brain is player-only): without
            // this gate the nearest-enemy routing let grunts hit grunts/boss,
            // which inflated the kill gate. Everyone else keeps free-for-all.
            bool gruntAttacker = GruntSpawner.IsGrunt(gameObject);
            HealthSystem best = null;
            float bestDist = float.MaxValue;
            foreach (var h in FindObjectsByType<HealthSystem>(FindObjectsSortMode.None))
            {
                if (h == null || h == health || h.IsDead) continue;
                if (gruntAttacker && !GruntSpawner.IsPlayer(h.gameObject)) continue;
                // The dormant boss (pre-combat idle at the edge) is untargetable:
                // nobody chases it and nobody can victory-kill it by accident.
                if (GruntSpawner.IsDormantBoss(h.gameObject)) continue;
                float d = Vector3.Distance(transform.position, h.transform.position);
                if (d <= range && d < bestDist) { bestDist = d; best = h; }
            }
            return best;
        }
    }
}
