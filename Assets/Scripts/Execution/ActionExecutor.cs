using System;
using System.Collections.Generic;
using System.Collections;
using Game.Actions;
using Game.Core;
using UnityEngine;

namespace Game.Execution
{
    // Executor-driven FSM: Enter (resolve + CrossFade) / Tick (time) / Exit.
    // Implements Core.IPlanRunner so TimeController drives it: LoadPlan() first,
    // then AcceptPlan(executor) -> StartPlan() runs the loaded queue.
    // Tick runs only while TickingEnabled (logical pause) and IsRunning.
    [DisallowMultipleComponent]
    public sealed class ActionExecutor : MonoBehaviour, IPlanRunner
    {
        [SerializeField] private ActionManifest manifest;
        [SerializeField] private Animator animator;

        // Programmatic locomotion: clips play in place (hips drift detrended,
        // root motion forced off), so the transform carries all displacement.
        // Run/Walk use tuned speeds matching the measured clip stride
        // (Run 3.59m/1.2s, Walk 1.41m/1.5s); one-shots use travelDistance.
        [SerializeField] private float runSpeed = 3.0f;
        [SerializeField] private float walkSpeed = 0.95f;
        // [F43] Per-actor Run/Walk multiplier: the boss must feel heavy but
        // relentless. Default 1 = player/grunts untouched; WireGruntSpawner
        // gives the boss's executor 1.4 (idempotent, reapplied by Wire Sandbox).
        [SerializeField] private float locomotionSpeedMultiplier = 1f;
        public float LocomotionSpeedMultiplier
        {
            get { return locomotionSpeedMultiplier; }
            set { locomotionSpeedMultiplier = Mathf.Max(0.1f, value); }
        }

        private IAnimationDriver driver;
        private ActionQueue queue;

        public ActionQueue Queue => queue ?? (queue = new ActionQueue(DefaultCapacity()));
        public ActionInstance Current { get; private set; }
        public bool IsRunning { get; private set; }

        // Entity state consulted by IsAvailable. Set by the owning character setup.
        public bool HasWeapon { get; set; }
        public float Stamina { get; set; } = 100f;
        public ActionRole Role { get; set; } = ActionRole.Any;

        // Logical-pause gate (set by integration from TimeController.ControllersTicking).
        public bool TickingEnabled { get; set; } = true;

        // Recovery cooldown: after a REACTION plan finishes, AI controllers must
        // wait until Time.time passes this before deciding again (user asked for
        // a random 0-5s breather). Consulted by BotController (wired), GruntController
        // and PlayerAutoAI (other owners). Range is inspector-tunable per fighter.
        [SerializeField] private Vector2 reactionRecoverySeconds = new Vector2(0f, 5f);
        public float RecoveryUntilTime { get; private set; }
        private bool runningReaction;
        private bool deathPlan;

        [Header("Death fade")]
        [SerializeField] private float deathFadeSeconds = 1.2f;

        public event Action PlanFinished;
        public event Action<ActionInstance> ActionStarted;
        public event Action<ActionInstance, ActionExitReason> ActionFinished;
        public event Action<string, string, string> ActionSubstituted;

        // Fired once per action at 50% of its duration (combat impact moment).
        public event Action<ActionInstance> ActionImpact;

        // Optional death gate, registered by CombatDirector. Dead fighters load nothing.
        public Game.Combat.HealthSystem Health { get; set; }

        private int DefaultCapacity() => manifest != null ? manifest.MaxPlanSize : ActionQueue.DefaultCapacity;

        private void Awake()
        {
            driver = new AnimatorDriver(animator != null ? animator : GetComponent<Animator>());
            queue = new ActionQueue(DefaultCapacity());
        }

        // Used by tests to inject a stub driver.
        public void BindDriver(IAnimationDriver animationDriver) => driver = animationDriver;
        public void BindManifest(ActionManifest actionManifest)
        {
            manifest = actionManifest;
            queue = new ActionQueue(DefaultCapacity());
        }

        // Pending reaction (queued hit/death/knockdown response). F8: damage
        // interrupts via director StopPlan + QueueReaction (immediate); a reaction
        // queued while busy still chains at FinishPlan. Death overwrites
        // HitReaction; hits collapse. Stored as canonical plan JSON so
        // multi-action reactions (knockdown) work.
        public string PendingReaction { get; private set; }

        public void QueueReaction(string actionId)
        {
            if (string.IsNullOrEmpty(actionId)) return;
            if (PendingReaction != null && !PendingReaction.Contains("\"type\":\"Death\"")
                && actionId == "Death")
            {
                PendingReaction = "{\"actions\":[{\"type\":\"Death\"}]}";
            }
            else if (PendingReaction == null)
            {
                PendingReaction = "{\"actions\":[{\"type\":\"" + actionId + "\"}]}";
            }
            // else: collapse (keep existing; Death already handled above).
            if (!IsRunning) PlayPendingReaction();
        }

        public void QueueReactionPlan(string planJson)
        {
            if (string.IsNullOrEmpty(planJson)) return;
            bool isDeath = planJson.Contains("\"type\":\"Death\"");
            if (PendingReaction == null || isDeath)
                PendingReaction = planJson;
            if (!IsRunning) PlayPendingReaction();
        }

        private void PlayPendingReaction()
        {
            string reaction = PendingReaction;
            PendingReaction = null;
            if (string.IsNullOrEmpty(reaction)) return;
            if (LoadPlan(reaction))
            {
                runningReaction = true;
                StartPlan();
            }
        }

        // Loads {"actions":[{"type":"AttackSword","target":"left"}]} into the queue.
        // Returns false on malformed JSON or missing manifest; never throws.
        public bool LoadPlan(string planJson)
        {
            // Cancel any in-flight plan first. The wheel can open while an
            // auto-AI plan or an immediate reaction is still playing (TryOpen
            // only checks the phase, not IsRunning); leaving IsRunning set
            // made StartPlan() a no-op -> stuck in Executing with
            // Current == null (player frozen, wheel unopenable). Every
            // LoadPlan caller either gates on !IsRunning or wants replacement.
            StopPlan();
            Queue.Clear(); // defensive: false-return paths must never leave residue
            if (Health != null && Health.IsDead) return false;
            if (manifest == null || string.IsNullOrEmpty(planJson)) return false;
            List<PlanEntry> entries;
            try { entries = PlanJsonReader.Read(planJson); }
            catch (Exception) { return false; }
            if (entries == null || entries.Count == 0) return false;
            if (entries.Count > manifest.MaxPlanSize) return false;
            foreach (var e in entries)
            {
                var def = manifest.FindByAlias(e.Type);
                if (def == null)
                {
                    Debug.LogWarning("[ActionExecutor] Unknown action '" + e.Type + "', skipping.");
                    continue;
                }
                MapTarget(e.Target, out var dir, out var targetId);
                Queue.TryEnqueue(new QueuedAction(def, dir, targetId));
            }
            return !Queue.IsEmpty;
        }

        // IPlanRunner: runs the previously loaded queue.
        public void StartPlan()
        {
            if (IsRunning) return;
            IsRunning = true;
            if (Queue.IsEmpty) { FinishPlan(); return; }
            EnterNext();
        }

        public void StopPlan()
        {
            if (!IsRunning) return;
            if (Current != null) ExitCurrent(ActionExitReason.Cancelled);
            Queue.Clear();
            IsRunning = false;
            runningReaction = false;
            deathPlan = false;
        }

        private void Update()
        {
            if (!IsRunning || !TickingEnabled) return;
            Tick(Time.deltaTime);
        }

        public void Tick(float deltaTime)
        {
            if (!IsRunning || !TickingEnabled || Current == null) return;
            float dt = Mathf.Max(0f, deltaTime);
            Current.Elapsed += dt;
            ApplyLocomotion(Current, dt);
            // [F7] Impact moment is per-action now (ImpactTimeFraction); the
            // 0.5 fallback covers a null definition defensively.
            var impactDef = Current.Action != null ? Current.Action.Definition : null;
            float impactFraction = impactDef != null ? impactDef.ImpactTimeFraction : 0.5f;
            if (!Current.ImpactFired && Current.Duration > 0f && Current.Elapsed >= Current.Duration * impactFraction)
            {
                Current.ImpactFired = true;
                ActionImpact?.Invoke(Current);
            }
            if (Current.IsComplete)
            {
                ExitCurrent(ActionExitReason.Completed);
                if (Queue.IsEmpty) FinishPlan();
                else EnterNext();
            }
        }

        // In-place clips never move the transform: displace it here, along the
        // current heading (rotation is owned by controllers/facing, not the executor).
        private void ApplyLocomotion(ActionInstance instance, float dt)
        {
            var def = instance?.Action?.Definition;
            if (def == null || dt <= 0f) return;
            float speed = 0f;
            // [F43] Per-actor locomotion multiplier applies to Run/Walk ONLY —
            // one-shot travel distances stay F5-calibrated (attack reach is
            // combat balance, not mood). Default 1 = player/grunts untouched.
            if (def.ActionId == "Run") speed = runSpeed * locomotionSpeedMultiplier;
            else if (def.ActionId == "Walk") speed = walkSpeed * locomotionSpeedMultiplier;
            else if (def.Duration > 0f) speed = def.TravelDistance / def.Duration;
            if (speed == 0f) return;
            transform.position += transform.forward * speed * dt;
            // [F52] A RETREAT walks BACKWARD (negative TravelDistance along
            // forward). Programmatic locomotion ignores physics, so a kiting
            // boss could back itself straight through the arena walls — clamp
            // X/Z inside the sandbox bounds after every retreat step.
            if (def.ActionId == "Retreat")
            {
                var p = transform.position;
                var clamped = new Vector3(
                    Mathf.Clamp(p.x, -RetreatArenaHalfExtent, RetreatArenaHalfExtent),
                    p.y,
                    Mathf.Clamp(p.z, -RetreatArenaHalfExtent, RetreatArenaHalfExtent));
                if (clamped != p) transform.position = clamped;
            }
        }

        // [F52] Sandbox walls sit at +-19.4 (SandboxBuilder arena, 0.5 thick);
        // clamping at 18.5 leaves ~0.65m so the boss rig stays clear while
        // backing up.
        private const float RetreatArenaHalfExtent = 18.5f;

        private void EnterNext()
        {
            if (!Queue.TryDequeue(out var queued) || queued == null)
            {
                FinishPlan();
                return;
            }
            var def = queued.Definition;
            if (!def.IsAvailable(HasWeapon, Role, Stamina))
            {
                var fallback = manifest.ResolveFallback(def, HasWeapon, Role, Stamina);
                string reason = "preconditions failed";
                if (fallback == null)
                {
                    Debug.LogWarning("[ActionExecutor] '" + def.ActionId + "' unavailable, no fallback. Skipped.");
                    ActionFinished?.Invoke(new ActionInstance(queued), ActionExitReason.Skipped);
                    EnterNext();
                    return;
                }
                Debug.LogWarning("[ActionExecutor] '" + def.ActionId + "' -> '" + fallback.ActionId + "' (" + reason + ").");
                ActionSubstituted?.Invoke(def.ActionId, fallback.ActionId, reason);
                queued = new QueuedAction(fallback, queued.Direction, queued.TargetId);
                def = fallback;
            }
            Current = new ActionInstance(queued);
            if (string.Equals(def.ActionId, "Death", StringComparison.OrdinalIgnoreCase))
                deathPlan = true;
            if (driver != null)
            {
                if (!driver.HasState(def.ActionId))
                    Debug.LogWarning("[ActionExecutor] Animator lacks state '" + def.ActionId + "'.");
                driver.Play(def.ActionId, def.UseRootMotion);
            }
            ActionStarted?.Invoke(Current);
        }

        private void ExitCurrent(ActionExitReason reason)
        {
            var done = Current;
            Current = null;
            if (done != null) ActionFinished?.Invoke(done, reason);
        }

        private void FinishPlan()
        {
            Current = null;
            IsRunning = false;
            // Chain a queued reaction instead of idling: the swing completed,
            // now the stagger/death plays. Single PlanFinished at the true end.
            if (!string.IsNullOrEmpty(PendingReaction))
            {
                PlayPendingReaction();
                return;
            }
            bool wasReaction = runningReaction;
            runningReaction = false;
            if (wasReaction)
            {
                float lo = Mathf.Min(reactionRecoverySeconds.x, reactionRecoverySeconds.y);
                float hi = Mathf.Max(reactionRecoverySeconds.x, reactionRecoverySeconds.y);
                RecoveryUntilTime = Time.time + UnityEngine.Random.Range(lo, hi);
            }
            // Death is terminal: leave the death pose visible through the end
            // of the clip, then fade the whole rig out. Never return to Idle.
            if (deathPlan)
            {
                deathPlan = false;
                StartCoroutine(FadeAndDisable());
                PlanFinished?.Invoke();
                return;
            }
            // Normal plans snap back to Idle in the same tick.
            if (driver != null && driver.HasState("Idle"))
                driver.Play("Idle", false);
            PlanFinished?.Invoke();
        }

        private IEnumerator FadeAndDisable()
        {
            var renderers = GetComponentsInChildren<Renderer>(true);
            var materials = new List<Material>();
            foreach (var renderer in renderers)
            {
                // renderer.materials creates per-renderer instances, so a
                // dying character cannot fade other fighters sharing the FBX.
                var instances = renderer.materials;
                for (int i = 0; i < instances.Length; i++)
                {
                    var material = instances[i];
                    if (material == null) continue;
                    materials.Add(material);
                    if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
                    if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
                    if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 0f);
                    material.renderQueue = 3000;
                }
            }

            float elapsed = 0f;
            while (elapsed < deathFadeSeconds)
            {
                elapsed += Time.deltaTime;
                float alpha = 1f - Mathf.Clamp01(elapsed / Mathf.Max(0.01f, deathFadeSeconds));
                foreach (var material in materials)
                {
                    if (material.HasProperty("_BaseColor"))
                    {
                        var color = material.GetColor("_BaseColor");
                        color.a = alpha;
                        material.SetColor("_BaseColor", color);
                    }
                    if (material.HasProperty("_Color"))
                    {
                        var color = material.GetColor("_Color");
                        color.a = alpha;
                        material.SetColor("_Color", color);
                    }
                }
                yield return null;
            }
            gameObject.SetActive(false);
        }

        private static void MapTarget(string target, out ActionDirection dir, out string targetId)
        {
            dir = ActionDirection.None;
            targetId = null;
            if (string.IsNullOrEmpty(target)) return;
            switch (target.Trim().ToLowerInvariant())
            {
                case "left": case "izquierda": dir = ActionDirection.Left; break;
                case "center": case "centro": dir = ActionDirection.Center; break;
                case "right": case "derecha": dir = ActionDirection.Right; break;
                default: targetId = target; break;
            }
        }
    }
}
