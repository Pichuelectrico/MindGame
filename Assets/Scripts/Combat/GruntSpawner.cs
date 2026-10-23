using System;
using System.Collections.Generic;
using Game.Actions;
using Game.AI;
using Game.Core;
using Game.Execution;
using Game.VFX;
using UnityEngine;

namespace Game.Combat
{
    // F6: NPC waves + the boss gate. Spawns one grunt every spawnInterval
    // (cap maxConcurrent alive): a clone of the player avatar tinted darker with
    // 5 HP and a lite heuristic brain. Kills are counted; after killsToWakeBoss
    // the boss leaves its MilitarySignaling_PreCombatIdle loop at the arena edge
    // and joins the fight (its BotController + CombatDirector are enabled then).
    // After the gate opens the spawner KEEPS spawning to sustain the fight —
    // deliberate: the arena always has a next wave once the mutant is out.
    [DisallowMultipleComponent]
    public sealed class GruntSpawner : MonoBehaviour
    {
        // Playtest 2026-10-14: first grunt at 5s, later waves every spawnInterval.
        [Tooltip("Segundos desde el inicio hasta el PRIMER grunt. Los siguientes usan spawnInterval.")]
        [SerializeField] private float firstSpawnDelay = 5f;
        [SerializeField] private float spawnInterval = 15f;
        [Tooltip("Cadencia de spawns tras despertar el boss (segundos).")]
        [SerializeField] private float bossSpawnInterval = 45f;
        [SerializeField] private int maxConcurrent = 3; // [F26] safe default even if the wire is ever lost
        [SerializeField] private int gruntHealth = 5;
        // [F18] Playtest: 5 NPCs antes de la entrada del boss. Antes era 10;
        // con el presupuesto de spawns pre-boss limitado a preBossSpawnLimit,
        // un gate de 10 kills nunca podría abrirse.
        [SerializeField] private int killsToWakeBoss = 5;
        [Tooltip("Máximo de grunts spawneados ANTES de la entrada del boss. La fase post-boss sigue en oleadas sostenidas (maxConcurrent).")]
        [SerializeField] private int preBossSpawnLimit = 8;
        [Tooltip("Probabilidad de spawnear un segundo grunt en la misma oleada (de vez en cuando salen 2 a la vez).")]
        [SerializeField] private float doubleSpawnChance = 0.4f;
        [SerializeField] private Vector3 arenaCenter = Vector3.zero;
        [SerializeField] private float arenaRadius = 14f;
        [SerializeField] private float minDistanceFromPlayer = 6f;
        [Tooltip("Corpse cleanup delay after a grunt dies (plays its Death plan first).")]
        [SerializeField] private float despawnSeconds = 6f;
        [SerializeField] private ActionManifest manifest;
        [SerializeField] private TimeController time;
        [SerializeField] private GameObject gruntTemplate;      // Player_YBot (cloned per grunt)
        [SerializeField] private Transform player;
        [SerializeField] private ActionExecutor playerExecutor;
        [SerializeField] private GameObject boss;               // Boss_Mutant root
        [SerializeField] private ActionExecutor bossExecutor;
        [SerializeField] private BotController bossController;  // starts DISABLED
        [SerializeField] private CombatDirector bossDirector;   // starts DISABLED
        [SerializeField] private string preCombatIdleId = "PreCombatIdle";
        [SerializeField] private string bossPowerUpId = "BossPowerUp"; // [F18] entrada del boss

        public int KillCount { get; private set; }
        public bool BossAwake { get; private set; }
        public int AliveCount => alive.Count;
        public int KillsToWakeBoss => killsToWakeBoss;
        public int TotalSpawned => nextId - 1; // [F18] budget pre-boss (tests + HUD)

        // Fires with the running total each time a grunt is defeated (HUD hook).
        public event Action<int> OnGruntKilled;

        private float timer;
        private int nextId = 1;
        private readonly List<GruntHandle> alive = new List<GruntHandle>();

        // Faction registry for combat targeting (playtest 2026-10-14: console
        // showed "Grunt_3 hits Grunt_1"). Grunt brains are player-only, but the
        // CombatDirector's nearest-enemy routing is a free-for-all — a stray
        // grunt-vs-grunt hit inflates the kill gate and wakes the boss early.
        // The spawner is the single source of truth for who is a grunt.
        private static readonly HashSet<GameObject> spawnedGrunts = new HashSet<GameObject>();

        // The player avatar root, captured at Awake; grunts may only damage it.
        public static Transform PlayerRoot { get; private set; }

        public static bool IsGrunt(GameObject go) => go != null && spawnedGrunts.Contains(go);

        public static bool IsPlayer(GameObject go) =>
            go != null && PlayerRoot != null &&
            (go == PlayerRoot.gameObject || go.transform.IsChildOf(PlayerRoot));

        // Boss registry (F8 playtest: the player-auto chased and punched the
        // DORMANT boss at the arena edge — and a stray hit could victory-kill
        // it via GameOverManager). One spawner per scene, so a static instance
        // cache is enough for these gates.
        private static GruntSpawner instance;
        private static GameObject bossRoot;

        public static bool IsBoss(GameObject go) =>
            go != null && bossRoot != null &&
            (go == bossRoot || go.transform.IsChildOf(bossRoot.transform));

        // Dormant = pre-combat (MilitarySignaling idle at the edge): NOT a valid
        // combat target for anyone — AI targeting and CombatDirector impacts
        // both skip it. Once WakeBoss fires, the boss is fair game.
        public static bool IsDormantBoss(GameObject go) =>
            IsBoss(go) && instance != null && !instance.BossAwake;

        private sealed class GruntHandle
        {
            public GameObject go;
            public GruntController controller;
        }

        // First grunt at firstSpawnDelay, then one per spawnInterval: start the
        // accumulator at spawnInterval - firstSpawnDelay so the first threshold
        // hit lands exactly at firstSpawnDelay (25 + 15s = 40s tick). Valid for
        // any firstSpawnDelay >= 0, even when it exceeds spawnInterval.
        private void Awake()
        {
            timer = spawnInterval - firstSpawnDelay;
            PlayerRoot = player;
            instance = this;
            var bossGo = bossDirector != null ? bossDirector.gameObject : (bossController != null ? bossController.gameObject : null);
            if (bossGo != null) bossRoot = bossGo;
        }

        private void Update()
        {
            // Dormant boss loops its pre-combat idle (design: MilitarySignaling_
            // PreCombatIdle) at the arena edge until the kill gate opens.
            if (!BossAwake && bossExecutor != null && !bossExecutor.IsRunning)
            {
                bossExecutor.LoadPlan("{\"actions\":[{\"type\":\"" + preCombatIdleId + "\"}]}");
                bossExecutor.StartPlan();
            }

            // Wave cadence: pre-boss uses spawnInterval, post-boss uses bossSpawnInterval (45s).
            float currentInterval = BossAwake ? bossSpawnInterval : spawnInterval;
            timer += Time.deltaTime;
            if (timer >= currentInterval)
            {
                timer = 0f;
                // [F18] Pre-boss budget: grunts keep spawning while under preBossSpawnLimit or KillCount < killsToWakeBoss;
                // once BossAwake the sustained waves resume indefinitely.
                bool spawnBudget = BossAwake || KillCount < killsToWakeBoss || nextId - 1 < preBossSpawnLimit;
                if (alive.Count < maxConcurrent && gruntTemplate != null && spawnBudget)
                {
                    SpawnGrunt();
                    // [F18] Spawn doble ocasional (de vez en cuando salen 2 a la vez) si hay capacidad y presupuesto.
                    // [F30] SOLO pre-boss: durante la boss fight la cadencia es exactamente 1 cada 45s.
                    spawnBudget = BossAwake || KillCount < killsToWakeBoss || nextId - 1 < preBossSpawnLimit;
                    if (!BossAwake && alive.Count < maxConcurrent && spawnBudget && UnityEngine.Random.value < doubleSpawnChance)
                    {
                        SpawnGrunt();
                    }
                }
            }

            // Logical pause sync: grunts freeze at 0 Hz with the rest (never timeScale).
            bool ticking = time == null || time.ControllersTicking;
            for (int i = alive.Count - 1; i >= 0; i--)
            {
                var g = alive[i];
                if (g.go == null) { alive.RemoveAt(i); continue; }
                if (g.controller != null) g.controller.Ticking = ticking;
            }
        }

        public void SpawnGrunt()
        {
            Vector3 pos = PickSpawnPoint();
            var go = Instantiate(gruntTemplate, pos, Quaternion.LookRotation(Flat(pos - arenaCenter)));
            go.name = "Grunt_" + nextId;
            spawnedGrunts.Add(go);
            StripPlayerOnly(go);

            var health = go.GetComponent<HealthSystem>();
            if (health != null) health.SetMaxHealth(gruntHealth);

            var controller = go.AddComponent<GruntController>();
            if (go.GetComponent<CharacterCollisionGate>() == null)
                go.AddComponent<CharacterCollisionGate>();
            controller.Configure(manifest, player, playerExecutor, "grunt" + nextId);
            Darken(go);
            // F23: runtime clones may not carry AttackTelegraph (TelegraphBuilder
            // only wires scene objects). Ensure after Darken so the flash binds
            // the same material instances the tint just created.
            EnsureAttackTelegraph(go);

            var captured = go;
            if (health != null)
                health.Died += _ => NotifyGruntDied(captured);

            alive.Add(new GruntHandle { go = go, controller = controller });
            nextId++;
            Debug.Log("[GruntSpawner] " + go.name + " spawned (" + alive.Count + " alive).");
        }

        // Kill counting + the boss gate. Public so tests can drive it without
        // waiting for real combat.
        public void NotifyGruntDied(GameObject grunt)
        {
            KillCount++;
            OnGruntKilled?.Invoke(KillCount);
            spawnedGrunts.Remove(grunt);
            alive.RemoveAll(g => g.go == grunt);
            if (grunt != null && despawnSeconds > 0f) Destroy(grunt, despawnSeconds);
            Debug.Log("[GruntSpawner] Grunt defeated. Kills: " + KillCount + "/" + killsToWakeBoss);
            if (!BossAwake && KillCount >= killsToWakeBoss) WakeBoss();
        }

        public void WakeBoss()
        {
            if (BossAwake) return;
            BossAwake = true;
            timer = 0f; // reset timer so first post-boss spawn follows bossSpawnInterval (45s)
            Debug.Log("[Boss] ¡El mutante entra al combate!");
            // [F18] Entrada con animación de power up: sustituimos el idle de
            // pre-combate por BossPowerUp (SwordShield_PowerUp retargeted al rig
            // del mutante). El reload del idle solo corre con !BossAwake y el
            // BotController no decide mientras executor.IsRunning, así el boss
            // interpreta el power up y solo después caza al jugador. Si la
            // acción no está en el manifest el executor solo avisa y salta el
            // paso (sin animación, mismo flujo de siempre).
            var bossExec = bossExecutor != null
                ? bossExecutor
                : boss != null ? boss.GetComponent<ActionExecutor>() : GetComponent<ActionExecutor>();
            if (bossExec != null)
            {
                bossExec.LoadPlan("{\"actions\":[{\"type\":\"" + bossPowerUpId + "\"}]}");
                bossExec.StartPlan();
            }
            // Serialized refs are wired by the editor menu; GetComponent
            // fallbacks keep the gate working even if wiring was skipped
            // (same rule as tests: build → wake must work out of the box).
            var director = bossDirector != null
                ? bossDirector
                : boss != null ? boss.GetComponent<CombatDirector>() : GetComponent<CombatDirector>();
            var controller = bossController != null
                ? bossController
                : boss != null ? boss.GetComponent<BotController>() : GetComponent<BotController>();
            if (director != null) director.enabled = true;
            if (controller != null) controller.enabled = true;
            if (boss != null && player != null)
            {
                Vector3 dir = player.position - boss.transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.0001f)
                    boss.transform.rotation = Quaternion.LookRotation(dir.normalized);
            }
        }

        // Ring spawn on the arena border, at least minDistanceFromPlayer away.
        private Vector3 PickSpawnPoint()
        {
            float fallback = 0f;
            for (int i = 0; i < 16; i++)
            {
                float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                Vector3 candidate = arenaCenter + new Vector3(
                    Mathf.Cos(angle) * arenaRadius, 0f, Mathf.Sin(angle) * arenaRadius);
                fallback = angle;
                if (player == null) return candidate;
                Vector3 toCandidate = candidate - player.position;
                toCandidate.y = 0f;
                if (toCandidate.magnitude >= minDistanceFromPlayer) return candidate;
            }
            // Degenerate case: ring point opposite the player.
            Vector3 away = player != null ? Flat(arenaCenter - player.position) : Vector3.back;
            return player != null
                ? player.position + away * Mathf.Max(arenaRadius, minDistanceFromPlayer)
                : arenaCenter;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 0.0001f ? Vector3.forward : v.normalized;
        }

        // Idempotent: AddComponent if missing. AttackTelegraph.Awake binds the
        // sibling ActionExecutor when the serialized ref is null.
        // public (not internal): Game.Tests is a separate assembly without
        // InternalsVisibleTo — F23's test calls this directly.
        public static AttackTelegraph EnsureAttackTelegraph(GameObject go)
        {
            if (go == null) return null;
            var telegraph = go.GetComponent<AttackTelegraph>();
            if (telegraph == null)
                telegraph = go.AddComponent<AttackTelegraph>();
            return telegraph;
        }

        // The grunt is a player-avatar clone: strip player-only control surface.
        // Type-name based to avoid a compile dependency on the GameMode assembly.
        // GameModeManager/TimeController/SandboxTestDriver MUST go too: they are
        // scene-level controllers mounted on the player — a second copy on each
        // grunt resets the game-mode machine (control loss at first spawn) and
        // duplicates the pause-time driver. (Playtest bug report #1, 2026-10-14.)
        private static void StripPlayerOnly(GameObject go)
        {
            var behaviours = go.GetComponents<MonoBehaviour>();
            for (int i = behaviours.Length - 1; i >= 0; i--)
            {
                var b = behaviours[i];
                if (b == null) continue;
                string type = b.GetType().Name;
                if (type == "PlayerManualController" || type == "PlayerFollowCamera"
                    || type == "GameModeManager" || type == "TimeController"
                    || type == "SandboxTestDriver")
                    Destroy(b);
            }
        }

        // Grunts read BLACK on purpose (playtest request #3): the arena floor and
        // the player avatar are light, so a near-black silhouette separates them.
        // URP Lit exposes _BaseColor (NOT _Color) — probe both to stay shader-safe.
        private static void Darken(GameObject go)
        {
            foreach (var renderer in go.GetComponentsInChildren<Renderer>())
            {
                var materials = renderer.materials;
                bool changed = false;
                foreach (var material in materials)
                {
                    if (material == null) continue;
                    if (material.HasProperty("_BaseColor"))
                    {
                        Color c = material.GetColor("_BaseColor");
                        material.SetColor("_BaseColor", new Color(
                            c.r * 0.18f, c.g * 0.18f, c.b * 0.22f, c.a));
                        changed = true;
                    }
                    else if (material.HasProperty("_Color"))
                    {
                        Color c = material.color;
                        material.color = new Color(
                            c.r * 0.18f, c.g * 0.18f, c.b * 0.22f, c.a);
                        changed = true;
                    }
                }
                if (changed) renderer.materials = materials;
            }
        }
    }

    // Lite AI for wave grunts: faces the player, closes distance, melee variety
    // via the shared BotBrain (PlayerOnly role -> BossOnly catalog stays gated).
    // Same decision-gating contract as BotController: never interrupts a running
    // action or a queued reaction; LLM-free by design (cheap waves).
    public sealed class GruntController : MonoBehaviour
    {
        [SerializeField] private float decisionInterval = 1.4f;

        private ActionExecutor executor;
        private HealthSystem health;
        private ActionManifest manifest;
        private Transform target;
        private ActionExecutor targetExecutor;
        private BotBrain brain;
        private string selfId = "grunt";
        private float timer;

        // Frozen by GruntSpawner with the logical pause (0 Hz while Planning).
        public bool Ticking { get; set; } = true;

        public void Configure(ActionManifest actionManifest, Transform targetTransform,
            ActionExecutor targetActionExecutor, string id)
        {
            manifest = actionManifest;
            target = targetTransform;
            targetExecutor = targetActionExecutor;
            selfId = id;
            executor = GetComponent<ActionExecutor>();
            health = GetComponent<HealthSystem>();
            brain = new BotBrain();
            timer = decisionInterval; // settle one beat before the first decision
        }

        private void Update()
        {
            if (!Ticking || manifest == null || executor == null) return;
            if (executor.IsRunning || executor.PendingReaction != null) return;
            if (Time.time < executor.RecoveryUntilTime) return; // post-reaction breather (0-5s random, F8)
            if (health != null && health.IsDead) return;
            if (target == null) return;
            var targetHealth = target != null ? target.GetComponent<HealthSystem>() : null;
            if (targetHealth != null && targetHealth.IsDead)
                return; // player down: stand by (corpse cleanup owns the scene)

            timer += Time.deltaTime;
            if (timer < decisionInterval) return;
            timer = 0f;

            FaceTarget();
            var snapshot = BuildSnapshot();
            string plan = brain.Decide(snapshot, manifest, selfId);
            if (string.IsNullOrEmpty(plan)) return;
            DecisionLogger.LogBotBrain(selfId, snapshot, plan);
            executor.LoadPlan(plan);
            executor.StartPlan();
        }

        private void FaceTarget()
        {
            Vector3 dir = target.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.LookRotation(dir.normalized);
        }

        // Two-entity snapshot: this grunt + the player. DTO only transports —
        // current action ids come from the live executors.
        private CombatStateSnapshot BuildSnapshot()
        {
            var self = new EntityState
            {
                id = selfId,
                name = gameObject.name,
                isBoss = false,
                isPlayerAvatar = false,
                position = ToPosition(transform),
                rotation = new float[3],
                health = health != null ? health.CurrentHealth : 0f,
                maxHealth = health != null ? health.MaxHealth : 0f,
                stamina = 100f,
                weaponEquipped = executor.HasWeapon,
                isTargetable = health == null || !health.IsDead,
                currentActionId = executor.Current != null
                    && executor.Current.Action != null
                    ? executor.Current.Action.Definition.ActionId
                    : string.Empty
            };
            self.isAttacking = CombatDirector.IsAttackId(self.currentActionId);

            var targetHealth = target != null ? target.GetComponent<HealthSystem>() : null;
            var targetEntity = new EntityState
            {
                id = "player",
                name = target != null ? target.name : "Player",
                isBoss = false,
                isPlayerAvatar = true,
                position = ToPosition(target),
                rotation = new float[3],
                health = targetHealth != null ? targetHealth.CurrentHealth : 0f,
                maxHealth = targetHealth != null ? targetHealth.MaxHealth : 0f,
                stamina = 100f,
                weaponEquipped = targetExecutor != null && targetExecutor.HasWeapon,
                isTargetable = targetHealth == null || !targetHealth.IsDead,
                currentActionId = targetExecutor != null && targetExecutor.Current != null
                    && targetExecutor.Current.Action != null
                    ? targetExecutor.Current.Action.Definition.ActionId
                    : string.Empty
            };
            targetEntity.isAttacking = CombatDirector.IsAttackId(targetEntity.currentActionId);

            return new CombatStateSnapshot
            {
                phase = Game.Core.CombatPhase.Running.ToString(),
                elapsedTime = Time.time,
                entities = new[] { self, targetEntity },
                distances = Array.Empty<EntityDistance>()
            };
        }

        private static float[] ToPosition(Transform t)
        {
            return t == null
                ? new float[3]
                : new[] { t.position.x, t.position.y, t.position.z };
        }
    }
}
