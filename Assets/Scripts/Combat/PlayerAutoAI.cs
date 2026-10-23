using System.Collections.Generic;
using Game.Actions;
using Game.AI;
using Game.Core;
using Game.Execution;
using Game.GameMode;
using UnityEngine;

namespace Game.Combat
{
    // [Auto-NPC] playtest 2026-10-14: "en modo automático el personaje principal
    // se vuelve un poste, y más bien debería ser un npc básico, a menos que le
    // dé con R instrucción a hacer algo".
    //
    // In Commands (auto) mode the player avatar behaves as a basic NPC: the
    // same BotBrain pipeline the boss uses decides a plan from the shared
    // ActionManifest and the player's own ActionExecutor runs it (the same
    // contract the R-wheel drives). Wheel instructions keep priority:
    // this component NEVER interrupts a running plan — it only fills idle
    // time — and waits a few ticks after any plan finishes before resuming.
    // In Manual mode it is fully passive (PlayerManualController rules).
    [DisallowMultipleComponent]
    public sealed class PlayerAutoAI : MonoBehaviour
    {
        [Tooltip("Cadencia de decisión cuando el executor está idle (segundos).")]
        [SerializeField] private float decisionInterval = 0.5f;

        [Tooltip("Ticks de gracia tras terminar cualquier plan (rueda o IA) antes de volver a decidir.")]
        [SerializeField] private int resumeGraceTicks = 2;

        [Tooltip("Manifiesto de acciones; el mismo asset que usa el boss/la rueda. Lo wirea SandboxBuilder.")]
        [SerializeField] private ActionManifest manifest;

        private GameModeManager modeManager;
        private TimeController time;
        private ActionExecutor executor;
        private HealthSystem health;
        private SwordManager sword;
        private readonly BotBrain brain = new BotBrain();
        private float decisionTimer;
        private int idleGraceTicks;

        private void Awake()
        {
            ResolveRefs();
        }

        private void ResolveRefs()
        {
            if (executor == null) executor = GetComponent<ActionExecutor>();
            if (health == null) health = GetComponent<HealthSystem>();
            if (time == null) time = FindFirstObjectByType<TimeController>();
            if (modeManager == null) modeManager = FindFirstObjectByType<GameModeManager>();
            if (sword == null) sword = FindFirstObjectByType<SwordManager>();
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        // Test seam: EditMode tests don't run Update/OnEnable, they call Tick directly.
        public void Tick(float dt)
        {
            if (executor == null || health == null) ResolveRefs(); // self-heal (also covers EditMode tests)
            if (executor == null || health == null || health.IsDead) return;

            // Manual mode: the human drives; completely passive.
            // NOTE: inside Game.Combat the bare identifier "GameMode" resolves to
            // the sibling NAMESPACE Game.GameMode (namespace beats using), so the
            // enum must be fully qualified here.
            if (modeManager != null && modeManager.CurrentMode != Game.GameMode.GameMode.Commands) return;

            // Logical pause (R-wheel Planning phase): controllers freeze at 0 Hz.
            if (time != null && !time.ControllersTicking) return;

            // Wheel override: an instruction (or a previous plan) is running or a
            // reaction is queued — hands off, never interrupt.
            if (executor.IsRunning || executor.PendingReaction != null)
            {
                idleGraceTicks = Mathf.Max(idleGraceTicks, resumeGraceTicks);
                decisionTimer = 0f;
                return;
            }

            // Grace hold after any plan finished (wheel or our own) so we never
            // re-decide in the same frame the plan ends.
            if (idleGraceTicks > 0)
            {
                idleGraceTicks--;
                return;
            }

            // [F8] Recovery: after reacting, the executor rests a random 0-5s
            // (ActionExecutor.RecoveryUntilTime) — no new decisions until it
            // clears. Tests override the read via RecoveryUntilTimeReader.
            float recoveryUntil = RecoveryUntilTimeReader != null
                ? RecoveryUntilTimeReader(executor)
                : executor.RecoveryUntilTime;
            if (Time.time < recoveryUntil)
            {
                decisionTimer = 0f;
                return;
            }

            decisionTimer += dt;
            if (decisionTimer < decisionInterval) return;
            decisionTimer = 0f;

            DecideAndRun();
        }

        // [F8] Recovery read seam. Tests (and future refactors) can inject a
        // reader; the default reads the real ActionExecutor.RecoveryUntilTime
        // (public getter, OpenCode's reaction-recovery). The seam exists because
        // the property's setter is private — tests can't set it directly.
        public System.Func<ActionExecutor, float> RecoveryUntilTimeReader;

        private void DecideAndRun()
        {
            if (manifest == null) return; // not wired yet; SandboxBuilder wires the shared asset

            var snapshot = BuildSnapshot();
            if (snapshot == null) return;

            string plan = brain.Decide(snapshot, manifest, "player");
            if (string.IsNullOrEmpty(plan)) return;

            DecisionLogger.LogBotBrain("player", snapshot, plan);

            if (executor.LoadPlan(plan)) executor.StartPlan();
        }

        // Same shape BotController builds for the boss: self + every living
        // combatant, distances measured from self. Self id is fixed "player"
        // (isBoss=false -> the brain restricts itself to PlayerOnly actions).
        private CombatStateSnapshot BuildSnapshot()
        {
            ResolveRefs();
            if (health == null || health.IsDead) return null;

            var entities = new List<EntityState>(8);
            var distances = new List<EntityDistance>(8);
            entities.Add(SelfEntity());

            Vector3 selfPos = transform.position;
            foreach (var hs in FindObjectsByType<HealthSystem>(FindObjectsSortMode.None))
            {
                if (hs == null || hs == health || hs.IsDead) continue;

                // [F8] Dormant boss gate (DECISION level): pre-combat the boss
                // idles at the arena edge — excluding it from the snapshot keeps
                // the auto-player in the grunt fight. The damage gate already
                // lives in CombatDirector; once WakeBoss fires, IsDormantBoss
                // flips and the boss becomes a normal target again.
                if (GruntSpawner.IsDormantBoss(hs.gameObject)) continue;

                var t = hs.transform;
                // Unique stable id per call; TargetId is informational only
                // (executor never resolves it against the scene).
                string id = hs.gameObject.name + "#" + hs.GetEntityId();
                var hsExec = hs.GetComponent<ActionExecutor>();

                entities.Add(new EntityState
                {
                    id = id,
                    name = hs.gameObject.name,
                    isBoss = false,
                    isPlayerAvatar = false,
                    position = new[] { t.position.x, t.position.y, t.position.z },
                    rotation = new[] { t.eulerAngles.x, t.eulerAngles.y, t.eulerAngles.z },
                    health = hs.CurrentHealth,
                    maxHealth = hs.MaxHealth,
                    stamina = hsExec != null ? hsExec.Stamina : 100f,
                    weaponEquipped = hsExec != null && hsExec.HasWeapon,
                    isTargetable = true,
                    currentActionId = hsExec != null && hsExec.IsRunning && hsExec.Current != null && hsExec.Current.Action != null && hsExec.Current.Action.Definition != null
                        ? hsExec.Current.Action.Definition.ActionId
                        : string.Empty
                });

                distances.Add(new EntityDistance
                {
                    fromId = "player",
                    toId = id,
                    distance = Vector3.Distance(selfPos, t.position)
                });
            }

            return new CombatStateSnapshot
            {
                phase = "Running",
                elapsedTime = Time.timeSinceLevelLoad,
                entities = entities.ToArray(),
                distances = distances.ToArray(),
                swordState = sword != null ? sword.State.ToString() : "Unknown",
                swordPosition = sword != null
                    ? new[] { sword.transform.position.x, sword.transform.position.y, sword.transform.position.z }
                    : new float[3]
            };
        }

        private EntityState SelfEntity()
        {
            return new EntityState
            {
                id = "player",
                name = gameObject.name,
                isBoss = false,
                isPlayerAvatar = true,
                position = new[] { transform.position.x, transform.position.y, transform.position.z },
                rotation = new[] { transform.eulerAngles.x, transform.eulerAngles.y, transform.eulerAngles.z },
                health = health.CurrentHealth,
                maxHealth = health.MaxHealth,
                stamina = executor != null ? executor.Stamina : 100f,
                weaponEquipped = executor != null && executor.HasWeapon,
                isTargetable = true
            };
        }
    }
}
