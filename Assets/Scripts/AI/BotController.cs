using Game.Actions;
using Game.Combat;
using Game.Core;
using Game.Execution;
using System.Collections.Generic;
using UnityEngine;

namespace Game.AI
{
    // Scene motor for the boss: every decisionInterval, while phase == Running
    // and its executor is free, builds a snapshot and lets BotBrain decide.
    // Damage is applied by CombatDirector on impact — never duplicated here.
    // Verified: BotBrain only emits manifest actionIds (HitReaction/Death included).
    [DisallowMultipleComponent]
    public sealed class BotController : MonoBehaviour
    {
        [SerializeField] private float decisionInterval = 0.8f; // [F43] 1.2 -> 0.8: relentless boss pacing
        [SerializeField] private ActionManifest manifest;
        [SerializeField] private ActionExecutor executor;
        [SerializeField] private HealthSystem health;
        [SerializeField] private Transform target;
        [SerializeField] private ActionExecutor targetExecutor;
        [SerializeField] private TimeController time;
        [SerializeField] private LlmConfig llmConfig;
        [SerializeField] private Game.Combat.SwordManager sword;

        private readonly BotBrain brain = new BotBrain();
        private readonly LlmBrain llmBrain = new LlmBrain();
        private float timer;
        private bool llmInFlight;
        private float llmFlightStarted;
        // Boss keeps fighting (interim heuristics) while the LLM is in flight.
        [SerializeField] private float llmGraceSeconds = 1.5f;
        // [F28] Wired by WireSandbox: lets the boss read the player's current action
        // (reactive throw-dodge) and measure real distance (approach-before-melee).
        [SerializeField] private ActionExecutor playerExecutor;
        [SerializeField] private float throwDodgeChance = 0.5f;
        // [F31] Reactive melee dodge: boss dodges 25% of player melee attacks.
        [SerializeField] private float meleeDodgeChance = 0.25f;
        [SerializeField] private float meleeDodgeCooldown = 1.5f;
        private ActionInstance lastPlayerActionSeen; // [F28] throw-poll freshness
        private ActionInstance lastPlayerActionSeenMelee; // [F31][lead fix] separate freshness — sharing one field made the melee poll consume throw actions' freshness and kill the 50% throw dodge
        private float lastMeleeDodgeTime;

        // [F45] Modo Furia: one-way boss enrage. enableFuria is OFF by default —
        // ONLY the boss bot turns it on (WireGruntSpawner). The first time HP drops
        // below furiaHealthFraction the boss latches: decision cadence
        // decisionInterval / furiaCadenceMultiplier (0.8s -> ~0.53s) and locomotion
        // speed x furiaSpeedMultiplier, composed FROM the base multiplier captured
        // at Bind/Start (F43 gives the sandbox boss 1.4 — never overwrite it hard).
        [SerializeField] private bool enableFuria = false;
        [SerializeField] private float furiaHealthFraction = 0.3f;
        [SerializeField] private float furiaCadenceMultiplier = 1.5f;
        [SerializeField] private float furiaSpeedMultiplier = 1.3f;
        private bool furiaLatched;
        private float furiaBaseSpeed = 1f;

        public bool IsFuria => enableFuria && furiaLatched;
        public float EffectiveDecisionInterval =>
            IsFuria ? decisionInterval / furiaCadenceMultiplier : decisionInterval;

        // [F46] Per-action cooldowns (seconds), tuned per action id — Unity can't
        // serialize Dictionary, so the serialized list is the source of truth and
        // the runtime dictionaries are built from it (lazily, EditMode-safe).
        // Defaults: only the boss laser recycles (10s). "opcionalmente expuesto
        // al snapshot": RemainingCooldown(actionId) is public — cheap to surface
        // later without changing the compact wire.
        [System.Serializable]
        private class ActionCooldownEntry
        {
            public string actionId;
            public float cooldownSeconds;
        }

        [SerializeField] private List<ActionCooldownEntry> actionCooldowns =
            new List<ActionCooldownEntry>
            {
                new ActionCooldownEntry { actionId = "BossLaserBeam", cooldownSeconds = 10f },
                new ActionCooldownEntry { actionId = "BossLaserTornado", cooldownSeconds = 14f } // [F47]
            };

        private readonly Dictionary<string, float> cooldownDurations =
            new Dictionary<string, float>();
        private readonly Dictionary<string, float> cooldownReadyAt =
            new Dictionary<string, float>();
        private ActionInstance lastOwnActionSeen; // one-instance watch: fires once per started action

        // [F51] Boss seeks health orbs at <= this HP fraction (never in fury).
        [SerializeField] private float seekHealthThreshold = 0.5f;
        private bool faceOrbPoint; // [F51] forward-only Run needs the facing on the orb
        private Vector3 orbPoint;

        // [F52] Decision modes cycled with the "\" key (Input System):
        // Llm (current behavior) -> ForcedHeuristic (ZERO LLM calls) ->
        // Strategist (the SECOND LLM prompt picks ONE persona word and the
        // heuristic algorithm executes). Default Llm = untouched behavior.
        public enum BossDecisionMode { Llm, ForcedHeuristic, Strategist }

        [SerializeField] private BossDecisionMode decisionMode = BossDecisionMode.Llm;
        public BossDecisionMode Mode => decisionMode;

        // [F52] Strategist: cached persona + re-ask cadence. The ask is fire-
        // and-forget — the boss ALWAYS decides via the algorithm with the
        // cached persona; a pending ask never blocks combat. Any failure /
        // timeout / garbage -> Normal (= classic behavior). None before the
        // first answer arrives (None also = classic rules).
        [SerializeField] private float strategistReaskSeconds = 6f;
        private float lastStrategistAsk = float.NegativeInfinity;
        // [AI-F8][lead] Variety guard against the "boss stuck in Distance" bug
        // (jsonl: Fighter ZERO times in the whole history): if the model
        // returns the same persona for more than maxPersonaHoldSeconds, the
        // guard forces a rotation (Distance->Fighter->Normal->Distance). The
        // LLM still decides at every ask — the guard only breaks sticky loops,
        // and every override is logged + flagged in the jsonl (forced:true).
        [SerializeField] private bool enforcePersonaVariety = true;
        [SerializeField] private float maxPersonaHoldSeconds = 20f;
        private float personaHeldSince = float.NegativeInfinity;
        private bool strategistAsking;
        private BossPersona currentPersona;
        public BossPersona Persona => currentPersona;
        public BossPersona CurrentPersona => currentPersona;

        // [UI-F5] Events triggered when LLM or Strategist decisions start and apply
        public event System.Action OnLlmDecisionStarted;
        public event System.Action OnLlmDecision;
        public bool IsLlmDeciding => llmInFlight || strategistAsking;

        public void SetLlmConfig(LlmConfig config) => llmConfig = config;

        // [F52] Inspection/test surface: LLM call counters. ForcedHeuristic
        // must keep them at ZERO; Strategist only ever bumps the strategy one
        // (the detailed prompt is never consulted in strategist mode).
        public int LlmDetailedCalls => llmBrain.DetailedCallCount;
        public int LlmStrategyCalls => llmBrain.StrategyCallCount;

        // Runtime wiring (spawner): plain setters, no subscriptions involved.
        public void Bind(ActionManifest m, ActionExecutor e, HealthSystem h, Transform t, TimeController tc)
        {
            manifest = m;
            executor = e;
            health = h;
            target = t;
            time = tc;
            // [F45] Capture the base locomotion multiplier AFTER spawner wiring
            // (SandboxBuilder sets 1.4 pre-Bind). Compose from base, never hardcode.
            if (!furiaLatched && executor != null)
                furiaBaseSpeed = executor.LocomotionSpeedMultiplier;
        }

        // [F45] Base-speed capture for inspector-wired bosses that never get Bind().
        private void Start()
        {
            if (!furiaLatched && executor != null)
                furiaBaseSpeed = executor.LocomotionSpeedMultiplier;
        }

        // [F45] One-way enrage latch: the first time HP < furiaHealthFraction the
        // boss enrages permanently. Cadence reads through EffectiveDecisionInterval;
        // speed composes from the captured base (sandbox boss: 1.4 x 1.3 = 1.82).
        public void EvaluateFuria()
        {
            if (!enableFuria || furiaLatched) return;
            if (health == null || health.MaxHealth <= 0) return;
            if (health.CurrentHealth >= health.MaxHealth * furiaHealthFraction) return;
            furiaLatched = true;
            if (executor != null)
                executor.LocomotionSpeedMultiplier = furiaBaseSpeed * furiaSpeedMultiplier;
            Debug.Log($"[Boss] ¡FURIA! HP {health.CurrentHealth}/{health.MaxHealth} — cadencia x{furiaCadenceMultiplier:0.00}, velocidad x{furiaSpeedMultiplier:0.00}.");
        }

        // [F46] Cooldown plumbing ------------------------------------------------

        private void EnsureCooldownTable()
        {
            if (cooldownDurations.Count > 0 || actionCooldowns == null) return;
            foreach (var e in actionCooldowns)
                if (e != null && !string.IsNullOrEmpty(e.actionId) && e.cooldownSeconds > 0f)
                    cooldownDurations[e.actionId] = e.cooldownSeconds;
        }

        // Called whenever the boss STARTS an action (polled by TrackOwnCooldowns;
        // public so EditMode tests and future event wiring can drive it).
        public void NoteActionStarted(string actionId)
        {
            EnsureCooldownTable();
            if (!cooldownDurations.TryGetValue(actionId, out float secs)) return;
            cooldownReadyAt[actionId] = Time.time + secs;
        }

        // Seconds until actionId is off cooldown — 0 when ready or not tracked.
        public float RemainingCooldown(string actionId)
        {
            EnsureCooldownTable();
            if (!cooldownDurations.ContainsKey(actionId)) return 0f;
            if (!cooldownReadyAt.TryGetValue(actionId, out float readyAt)) return 0f;
            return Mathf.Max(0f, readyAt - Time.time);
        }

        // One "instance seen" watch: the executor holds a single Current, so a
        // ReferenceEquals against the last seen instance fires exactly once per
        // started action — same proven pattern as the F31 player action polls.
        private void TrackOwnCooldowns()
        {
            var cur = executor != null ? executor.Current : null;
            if (ReferenceEquals(cur, lastOwnActionSeen)) return;
            lastOwnActionSeen = cur;
            if (cur?.Action?.Definition != null)
                NoteActionStarted(cur.Action.Definition.ActionId);
        }

        private void Update()
        {
            // [F52] "\" cycles the decision mode in ANY phase (debug control).
            if (UnityEngine.InputSystem.Keyboard.current != null
                && UnityEngine.InputSystem.Keyboard.current.backslashKey.wasPressedThisFrame)
                CycleMode();
            if (executor == null || health == null || manifest == null) return;
            if (time != null && time.Current != CombatPhase.Running) return; // no ticks in Planning/Executing
            TrackOwnCooldowns(); // [F46] mark started actions as on-cooldown (once per instance)
            EvaluateFuria(); // [F45] latch fires mid-combat, even mid-action
            PollPlayerMeleeReaction();
            PollPlayerThrowReaction();
            if (executor.IsRunning || executor.PendingReaction != null || health.IsDead) return;
            if (Time.time < executor.RecoveryUntilTime) return; // post-reaction breather

            if (llmInFlight)
            {
                // [Anti-freeze] While the LLM thinks (up to timeout x retries), the
                // boss must NOT stand still: run quick heuristic actions after a
                // grace period. qwen3 with /no_think answers in ~3-4s; a cold Ollama
                // (model reload after keep-alive expiry) can take 30s+ — the interim
                // path keeps combat alive either way.
                if (Time.time - llmFlightStarted < llmGraceSeconds) return;
                timer = 0f;
                RunHeuristic(BuildSnapshot(), "[Boss] interim heuristic (LLM in flight).");
                return;
            }

            timer += Time.deltaTime;
            if (timer < EffectiveDecisionInterval) return; // [F45] fury: 0.8s -> ~0.53s
            timer = 0f;
            DecideAndRun();
        }

        // [F52] public for EditMode tests: "ForcedHeuristic makes ZERO LLM
        // calls" and "Strategist runs the heuristic with the cached persona"
        // both need a decision entry point without a keyboard.
        public void DecideAndRun()
        {
            var snapshot = BuildSnapshot();
            if (decisionMode == BossDecisionMode.ForcedHeuristic)
            {
                RunHeuristic(snapshot, "[Boss] heuristic (ForcedHeuristic mode).");
                return;
            }
            if (decisionMode == BossDecisionMode.Strategist)
            {
                _ = MaybeAskStrategistAsync(snapshot); // refresh persona on its own cadence
                RunHeuristic(snapshot, "[Boss] strategist (" + PersonaName() + ").");
                return;
            }
            if (UseLlm())
            {
                // Async: the boss keeps its current plan while the LLM answers.
                llmInFlight = true;
                llmFlightStarted = Time.time;
                _ = DecideWithLlmAsync(snapshot);
                return;
            }
            RunHeuristic(snapshot, "[Boss] heuristic (no LLM config)");
        }

        // [UI-F5] Async entry point for EditMode tests and async callers.
        public async System.Threading.Tasks.Task DecideAndRunAsync()
        {
            var snapshot = BuildSnapshot();
            if (decisionMode == BossDecisionMode.ForcedHeuristic)
            {
                RunHeuristic(snapshot, "[Boss] heuristic (ForcedHeuristic mode).");
                return;
            }
            if (decisionMode == BossDecisionMode.Strategist)
            {
                await MaybeAskStrategistAsync(snapshot);
                RunHeuristic(snapshot, "[Boss] strategist (" + PersonaName() + ").");
                return;
            }
            if (UseLlm())
            {
                llmInFlight = true;
                llmFlightStarted = Time.time;
                await DecideWithLlmAsync(snapshot);
                return;
            }
            RunHeuristic(snapshot, "[Boss] heuristic (no LLM config)");
        }

        private bool UseLlm()
        {
            return llmConfig != null && !string.IsNullOrEmpty(llmConfig.EndpointUrl);
        }

        // [F52] Llm -> ForcedHeuristic -> Strategist -> Llm. Public so EditMode
        // tests (and a future UI button) can drive it without a keyboard.
        public void CycleMode()
        {
            decisionMode = decisionMode == BossDecisionMode.Llm ? BossDecisionMode.ForcedHeuristic
                : decisionMode == BossDecisionMode.ForcedHeuristic ? BossDecisionMode.Strategist
                : BossDecisionMode.Llm;
            string personaPart = decisionMode == BossDecisionMode.Strategist
                ? " (persona: " + PersonaName() + ")" : "";
            Debug.Log("[Boss] Modo: " + ModeName(decisionMode) + personaPart);
        }

        // [F52] Display names for the [Boss] Modo log.
        private static string ModeName(BossDecisionMode mode)
        {
            switch (mode)
            {
                case BossDecisionMode.ForcedHeuristic: return "Heuristico";
                case BossDecisionMode.Strategist: return "Estratega";
                default: return "LLM";
            }
        }

        // [F52] Persona display name (None shows as Normal — same rules).
        private string PersonaName()
        {
            switch (currentPersona)
            {
                case BossPersona.Distance: return "Distance";
                case BossPersona.Fighter: return "Fighter";
                default: return "Normal";
            }
        }

        // [F52] Fire-and-forget persona refresh: asks the strategist LLM for
        // ONE word every strategistReaskSeconds. The boss keeps deciding via
        // the algorithm with the CACHED persona meanwhile — the ask never
        // blocks combat. The re-ask window uses Time.time, so EditMode tests
        // (constant time) see at most one ask per DecideAndRun call.
        // [AI-F8][lead] Three changes vs F52 (root cause: qwen2.5:7b is
        // sticky — it answered "Distance" on every re-ask, Fighter NEVER
        // appeared, so the boss froze in one mode for whole playtests):
        // 1. Transport down / timeout now KEEPS the cached persona instead of
        //    snapping to Normal (stable HUD, no churn; first ask starts Normal).
        // 2. Persona self-awareness: the ask passes current persona + hold
        //    time so the model knows what it has been doing and can switch.
        // 3. Variety guard + raw telemetry: every ask is persisted to the
        //    jsonl (boss_strategy event: persona, raw answer, latency, hold,
        //    forced flag) — before this the strategist was a black box.
        private async System.Threading.Tasks.Task MaybeAskStrategistAsync(
            CombatStateSnapshot snapshot)
        {
            if (strategistAsking) return;
            if (Time.time - lastStrategistAsk < strategistReaskSeconds) return;
            strategistAsking = true;
            lastStrategistAsk = Time.time;
            OnLlmDecisionStarted?.Invoke();
            float heldSeconds = Time.time - personaHeldSince;
            if (float.IsNegativeInfinity(heldSeconds)) heldSeconds = 0f;
            BossPersona previous = currentPersona;
            bool forced = false;
            try
            {
                BossPersona answer = await llmBrain.DecideStrategyAsync(
                    snapshot, llmConfig, null, previous, heldSeconds);
                currentPersona = answer;
                if (enforcePersonaVariety && answer == previous
                    && previous != BossPersona.None
                    && heldSeconds >= maxPersonaHoldSeconds)
                {
                    currentPersona = LlmBrain.NextVarietyPersona(previous);
                    forced = true;
                }
                if (forced || currentPersona != previous)
                    personaHeldSince = Time.time;
                Debug.Log("[Boss] Estratega: persona " + PersonaName()
                    + (forced ? " (variedad forzada tras " + heldSeconds.ToString("0")
                        + "s en " + LlmBrain.PersonaWord(previous) + ")" : "")
                    + " · raw=\"" + (llmBrain.LastStrategyRaw ?? "<sin respuesta>")
                    + "\" · " + llmBrain.LastStrategyLatencyMs + "ms.");
                DecisionLogger.LogStrategist(PersonaName(), llmBrain.LastStrategyRaw,
                    llmBrain.LastStrategyLatencyMs, heldSeconds, forced);
            }
            catch (System.Exception)
            {
                // [AI-F8][lead] Keep the cached persona on failure — a stale
                // persona is better than churn; the ask retries on cadence.
                Debug.LogWarning("[Boss] Estratega: fallo de transporte, se mantiene persona "
                    + PersonaName() + ".");
            }
            finally
            {
                strategistAsking = false;
                OnLlmDecision?.Invoke();
            }
        }

        private async System.Threading.Tasks.Task DecideWithLlmAsync(CombatStateSnapshot snapshot)
        {
            OnLlmDecisionStarted?.Invoke();
            var started = System.Diagnostics.Stopwatch.StartNew();
            LlmBrain.LlmDecision decision;
            try
            {
                decision = await llmBrain.DecideDetailedAsync(snapshot, manifest, llmConfig);
            }
            finally
            {
                llmInFlight = false;
            }
            started.Stop();
            try
            {
                var plan = decision.Plan;
                plan = EnsureApproachBeforeMelee(plan);
                if (string.IsNullOrEmpty(plan))
                {
                    LogLlmFallbackReason(decision, started.ElapsedMilliseconds);
                    RunHeuristic(snapshot, "[Boss] heuristic fallback.");
                    return;
                }
                if (executor.IsRunning || health.IsDead
                    || (time != null && time.Current != CombatPhase.Running))
                {
                    // The interim heuristic is mid-action — the LLM plan is stale by now.
                    Debug.Log("[Boss] LLM plan arrived late (" + started.ElapsedMilliseconds +
                        " ms) — interim action running, plan discarded.");
                    return;
                }
                if (executor.LoadPlan(plan))
                {
                    DecisionLogger.LogLlm("boss", snapshot, plan, llmConfig.ModelName,
                        llmConfig.EndpointUrl, started.ElapsedMilliseconds, decision.Raw, llmBrain.LastPayloadBytes);
                    UpdateOrbFacing(plan); // [F51] steer the forward-only orb Run (LLM path)
                    Debug.Log("[Boss] LLM plan.");
                    executor.StartPlan();
                    return;
                }
                LogLlmFallbackReason(decision, started.ElapsedMilliseconds);
                RunHeuristic(snapshot, "[Boss] heuristic fallback.");
            }
            finally
            {
                OnLlmDecision?.Invoke();
            }
        }

        private void LogLlmFallbackReason(LlmBrain.LlmDecision decision, long latencyMs)
        {
            if (string.IsNullOrEmpty(decision.Raw))
            {
                Debug.LogWarning("[Boss] LLM fallback (" + latencyMs + " ms): transport failed / empty response.");
                return;
            }
            string raw = decision.Raw.Length > 400 ? decision.Raw.Substring(0, 400) + "…" : decision.Raw;
            Debug.LogWarning("[Boss] LLM fallback (" + latencyMs + " ms): plan not applied. raw=" + raw);
        }

        // [F29] If the LLM opens with a melee action while the player is out of reach,
        // prepend Run so the boss closes in instead of swinging at the air.
        private string EnsureApproachBeforeMelee(string plan)
        {
            if (string.IsNullOrEmpty(plan) || plan.IndexOf("\"type\":\"Run\"", System.StringComparison.Ordinal) >= 0)
                return plan;
            int typeIdx = plan.IndexOf("\"type\":\"", System.StringComparison.Ordinal);
            if (typeIdx < 0) return plan;
            int start = typeIdx + 8;
            int end = plan.IndexOf('"', start);
            if (end < 0) return plan;
            var firstId = plan.Substring(start, end - start);
            var def = manifest != null ? manifest.Find(firstId) : null;
            if (def == null || def.Range <= 0f) return plan; // not a ranged-limited (melee) action
            float dist = playerExecutor != null
                ? Vector3.Distance(transform.position, playerExecutor.transform.position)
                : float.MaxValue;
            if (dist <= def.Range) return plan;
            Debug.Log("[Boss] LLM opens with " + firstId + " at " + dist.ToString("F1") +
                "m > range " + def.Range.ToString("F1") + "m — prepending Run.");
            return plan.Replace("{\"actions\":[", "{\"actions\":[{\"type\":\"Run\",\"target\":\"player\"},");
        }

        // [F28] Reactive dodge: when the player starts a sword-throw, roll 50% and
        // dodge — reported on the LLM dashboard as a boss "thought".
        private void PollPlayerThrowReaction()
        {
            if (playerExecutor == null || executor == null || health == null || health.IsDead) return;
            var current = playerExecutor.Current;
            if (current == null || ReferenceEquals(current, lastPlayerActionSeen)) return;
            lastPlayerActionSeen = current;
            var actionId = current.Action != null && current.Action.Definition != null
                ? current.Action.Definition.ActionId
                : null;
            if (string.IsNullOrEmpty(actionId) || actionId.IndexOf("Throw", System.StringComparison.OrdinalIgnoreCase) < 0)
                return;
            if (Random.value > throwDodgeChance)
            {
                Debug.Log("[Boss] Player " + actionId + " -> dodge roll missed (50%).");
                return;
            }
            executor.QueueReaction("BossDodge");
            DecisionLogger.LogLlm("boss", BuildSnapshot(),
                "{\"actions\":[{\"type\":\"BossDodge\"}]}",
                llmConfig != null ? llmConfig.ModelName : "boss-reflex",
                llmConfig != null ? llmConfig.EndpointUrl : "-",
                0,
                "{\"note\":\"reactive dodge: 50% roll triggered by player sword-throw (" + actionId + ")\"}");
            Debug.Log("[Boss] Player " + actionId + " -> 50% dodge roll HIT: dodging (reported as LLM thought).");
        }

        // [F31] Reactive melee dodge: when the player starts a melee attack (punch/kick/slash/heavy),
        // roll 25% and dodge with BossDodge animation — reported on the LLM dashboard as a boss "thought".
        private void PollPlayerMeleeReaction()
        {
            if (playerExecutor == null || executor == null || health == null || health.IsDead) return;
            var current = playerExecutor.Current;
            if (current == null || ReferenceEquals(current, lastPlayerActionSeenMelee)) return;
            lastPlayerActionSeenMelee = current;
            var actionId = current.Action != null && current.Action.Definition != null
                ? current.Action.Definition.ActionId
                : null;
            if (string.IsNullOrEmpty(actionId)) return;
            // Melee only: contains Punch/Kick/Slash/Heavy/Attack but NOT Throw.
            bool isMelee = actionId.IndexOf("Punch", System.StringComparison.OrdinalIgnoreCase) >= 0
                || actionId.IndexOf("Kick", System.StringComparison.OrdinalIgnoreCase) >= 0
                || actionId.IndexOf("Slash", System.StringComparison.OrdinalIgnoreCase) >= 0
                || actionId.IndexOf("Heavy", System.StringComparison.OrdinalIgnoreCase) >= 0
                || actionId.IndexOf("Attack", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool isThrow = actionId.IndexOf("Throw", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isMelee || isThrow) return;
            // Cooldown between reactive dodges.
            if (Time.time - lastMeleeDodgeTime < meleeDodgeCooldown) return;
            if (UnityEngine.Random.value > meleeDodgeChance)
            {
                Debug.Log("[Boss] Player " + actionId + " -> melee dodge roll missed (25%).");
                return;
            }
            lastMeleeDodgeTime = Time.time;
            executor.QueueReaction("BossDodge");
            DecisionLogger.LogLlm("boss", BuildSnapshot(),
                "{\"actions\":[{\"type\":\"BossDodge\"}]}",
                llmConfig != null ? llmConfig.ModelName : "boss-reflex",
                llmConfig != null ? llmConfig.EndpointUrl : "-",
                0,
                "{\"note\":\"melee-dodge: 25% roll triggered by player melee (" + actionId + ")\"}");
            Debug.Log("[Boss] Player " + actionId + " -> 25% melee dodge roll HIT: dodging (reported as LLM thought).");
        }

        private void RunHeuristic(CombatStateSnapshot snapshot, string log)
        {
            brain.Furious = IsFuria; // [F45] enrage: BossChain prioritizes Heavy
            brain.Persona = decisionMode == BossDecisionMode.Strategist // [F52]
                ? currentPersona : BossPersona.None; // persona ONLY lives in Estratega mode
            brain.RemainingCooldownOf = RemainingCooldown; // [F46] per-action cooldown probe
            brain.SeekHealthThreshold = seekHealthThreshold; // [F51] serialized knob
            string plan = brain.Decide(snapshot, manifest, "boss");
            if (string.IsNullOrEmpty(plan)) return;
            DecisionLogger.LogBotBrain("boss", snapshot, plan);
            UpdateOrbFacing(plan); // [F51] steer the forward-only orb Run
            if (executor.LoadPlan(plan))
            {
                Debug.Log(log);
                executor.StartPlan();
            }
        }

        // [F51] Orb-approach steering: locomotion is forward-only (rotation is
        // owned by facing), so when the APPLIED plan opens with a Run whose
        // target is NOT "player" and the boss is wounded + not furious, that
        // run is a health-orb approach — face the nearest active orb so the
        // forward run closes on it. Reset on every applied plan (heuristic
        // and LLM paths both call this right before LoadPlan).
        private void UpdateOrbFacing(string plan)
        {
            faceOrbPoint = false;
            if (health == null || health.MaxHealth <= 0 || IsFuria) return;
            if (health.CurrentHealth > health.MaxHealth * seekHealthThreshold) return;
            if (string.IsNullOrEmpty(plan)) return;
            var parsed = LlmPlanResponse.FromJson(plan);
            if (parsed == null || !parsed.IsValid() || parsed.actions.Length == 0) return;
            var first = parsed.actions[0];
            if (first.type != "Run" || string.IsNullOrEmpty(first.target)
                || first.target == "player") return;
            var orb = Game.Combat.HealthPickup.NearestTo(transform.position);
            if (orb == null) return;
            faceOrbPoint = true;
            orbPoint = orb.transform.position;
        }

        private void FaceTarget()
        {
            FacePoint(target.position);
        }

        private void FacePoint(Vector3 point)
        {
            Vector3 to = point - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 0.001f) return;
            transform.rotation = Quaternion.LookRotation(to.normalized);
        }

        private CombatStateSnapshot BuildSnapshot()
        {
            var targetHealth = target != null ? target.GetComponent<HealthSystem>() : null;
            bool targetDead = targetHealth != null && targetHealth.IsDead;
            bool bossWeapon = executor != null && executor.HasWeapon;
            Vector3 swordPos = sword != null ? sword.SwordPosition : transform.position + Vector3.forward * 5f;
            bool swordUpForGrabs = sword != null && sword.State == Game.Combat.SwordState.InMap && !bossWeapon;

            // Face the living target so Run actually closes distance — or the
            // loose sword when unarmed and it's up for grabs (auto-pickup) —
            // or the health orb while the [F51] orb-approach plan is live.
            if (target != null && !targetDead)
            {
                if (faceOrbPoint)
                    FacePoint(orbPoint);
                else if (swordUpForGrabs)
                    FacePoint(swordPos);
                else
                    FaceTarget();
            }

            var bossPos = transform.position;
            Vector3 playerPos = target != null ? target.position : bossPos + Vector3.forward * 5f;
            float dist = Vector3.Distance(bossPos, playerPos);

            // What the player is doing right now (enriched snapshot: the LLM sees attacks).
            string playerAction = string.Empty;
            bool playerAttacking = false;
            var current = targetExecutor != null ? targetExecutor.Current : null;
            var playerDef = current != null && current.Action != null ? current.Action.Definition : null;
            if (playerDef != null)
            {
                playerAction = playerDef.ActionId ?? string.Empty;
                playerAttacking = Game.Combat.CombatDirector.IsAttackId(playerAction);
            }

            return new CombatStateSnapshot
            {
                phase = time != null ? time.Current.ToString() : "Running",
                elapsedTime = Time.time,
                entities = new[]
                {
                    new EntityState
                    {
                        id = "boss", name = "Mutant", isBoss = true,
                        position = new[] { bossPos.x, bossPos.y, bossPos.z },
                        rotation = new[] { 0f, transform.eulerAngles.y, 0f },
                        health = health.CurrentHealth, maxHealth = health.MaxHealth,
                        stamina = executor.Stamina, weaponEquipped = bossWeapon,
                        isTargetable = true,
                        furious = IsFuria, // [F45] LLM sees the enrage (wire: only when true)
                        availableActions = new string[0]
                    },
                    new EntityState
                    {
                        id = "player", name = "Y Bot", isPlayerAvatar = true,
                        position = new[] { playerPos.x, playerPos.y, playerPos.z },
                        rotation = new float[3],
                        health = targetHealth != null ? targetHealth.CurrentHealth : 10f,
                        maxHealth = targetHealth != null ? targetHealth.MaxHealth : 10,
                        stamina = 100f,
                        weaponEquipped = targetExecutor != null && targetExecutor.HasWeapon,
                        // Dead target is not a threat: BotBrain yields IdleFight (fight over).
                        isTargetable = !targetDead,
                        currentActionId = playerAction,
                        isAttacking = playerAttacking,
                        availableActions = new string[0]
                    },
                },
                distances = new[]
                {
                    new EntityDistance { fromId = "boss", toId = "player", distance = dist }
                },
                swordState = sword != null ? sword.State.ToString() : "Unknown",
                swordPosition = sword != null
                    ? new[] { sword.SwordPosition.x, sword.SwordPosition.y, sword.SwordPosition.z }
                    : new float[3]
            };
        }
    }
}
