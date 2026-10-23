using System;
using System.Text;
using System.Threading.Tasks;
using Game.Actions;

namespace Game.AI
{
    // Pure boss LLM brain over the F3 LlmClient. Never throws: any failure
    // (timeout, error, empty/invalid plan) yields null and the caller falls
    // back to BotBrain. The game never waits on the LLM.
    public sealed class LlmBrain
    {
        // [F14.2] Plan + raw transport body for DecisionLogger. Raw is the
        // model's literal output (null when nothing arrived, e.g. transport
        // failure). Plan null => caller falls back to heuristic BotBrain.
        public readonly struct LlmDecision
        {
            public readonly string Plan;
            public readonly string Raw;
            public LlmDecision(string plan, string raw) { Plan = plan; Raw = raw; }
        }

        public async Task<string> DecideAsync(CombatStateSnapshot snapshot,
            ActionManifest manifest, LlmConfig config, ILlmTransport transport = null)
        {
            var decision = await DecideDetailedAsync(snapshot, manifest, config, transport);
            return decision.Plan;
        }

        /// <summary>[F38][lead] Exact request-body bytes of the most recent DecideDetailedAsync
        /// attempt (0 if not measured). Lets the caller persist payloadBytes into the jsonl.</summary>
        public int LastPayloadBytes { get; private set; }

        // ===== [F52] Strategist: SECOND minimal prompt =====================

        // [F52] Call counters so EditMode tests can assert "ForcedHeuristic
        // makes ZERO LLM calls" and "Strategist asks on its own cadence"
        // without touching transport internals.
        public int DetailedCallCount { get; private set; }
        public int StrategyCallCount { get; private set; }

        // [F52] Strategist decision: a SECOND, minimal prompt (NEVER touches
        // DecideDetailedAsync) that asks the model for ONE persona word.
        // Any failure/timeout/garbage -> Normal (classic behavior). The full
        // plan stays with the heuristic algorithm — the strategist only
        // chooses HOW to fight, the algorithm still executes.
        // [AI-F8][lead] Optional persona context (current persona + how long it
        // has been held): qwen2.5:7b answered "Distance" on every re-ask and
        // Fighter NEVER appeared in the whole llm-decisions.jsonl history — the
        // boss froze in one mode for whole playtests. The model now knows what
        // it is doing and gets variety pressure. Raw reply + latency exposed
        // (same pattern as LastPayloadBytes) so BotController persists the
        // strategist ask into the jsonl — until now it was a black box.
        public string LastStrategyRaw { get; private set; }
        public long LastStrategyLatencyMs { get; private set; }

        public async Task<BossPersona> DecideStrategyAsync(CombatStateSnapshot snapshot,
            LlmConfig config, ILlmTransport transport = null,
            BossPersona currentPersona = BossPersona.None, float heldSeconds = 0f)
        {
            StrategyCallCount++;
            if (snapshot == null || config == null
                || string.IsNullOrEmpty(config.EndpointUrl)) return BossPersona.Normal;
            try
            {
                var client = new LlmClient(config, transport);
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                string raw = await client.SendRawAsync(
                    BuildStrategyPrompt(),
                    BuildStrategyContext(snapshot, currentPersona, heldSeconds));
                stopwatch.Stop();
                LastStrategyRaw = raw;
                LastStrategyLatencyMs = stopwatch.ElapsedMilliseconds;
                return ParsePersona(raw);
            }
            catch (Exception)
            {
                LastStrategyRaw = null;
                LastStrategyLatencyMs = 0;
                return BossPersona.Normal;
            }
        }

        // [F52] /no_think stays FIRST (same StartsWith contract as the main
        // prompt). Deliberately tiny: one decision in, one word out.
        // [AI-F8][lead] One variety sentence added (F38 diet respected): the
        // small model is sticky — without pressure it repeats the same persona
        // phase after phase ("Distance" dominated every recorded session).
        public static string BuildStrategyPrompt() =>
            "/no_think\n" +
            "You are the duel strategist for the Mutant boss. Read the state and pick ONE combat persona for this phase:\n" +
            "- Distance: keep away, fight with lasers, back off when pressured.\n" +
            "- Fighter: close in, brawl with the sword, throw it from mid range.\n" +
            "- Normal: balanced melee aggression.\n" +
            "Do not repeat the same persona phase after phase: vary your choice to keep the duel unpredictable unless the situation truly demands it.\n" +
            "Answer with EXACTLY one word: Distance, Fighter or Normal.";

        // [F52] Exactly the context the task asks for: distance, HPs, sword
        // state, orb availability — 4 short lines, no action catalog.
        // [AI-F8][lead] Optional persona awareness (appended only when the
        // caller passes a non-None persona, so first asks and legacy callers
        // keep the 4-line shape): the model sees which persona it has been
        // holding and for how long — the minimum nudge it needs to switch.
        public static string BuildStrategyContext(CombatStateSnapshot snapshot,
            BossPersona currentPersona = BossPersona.None, float heldSeconds = 0f)
        {
            EntityState boss = null;
            EntityState player = null;            if (snapshot != null && snapshot.entities != null)
            {
                foreach (var e in snapshot.entities)
                {
                    if (e == null) continue;
                    if (e.isBoss && boss == null) boss = e;
                    else if (!e.isBoss && player == null) player = e;
                }
            }
            float dist = DistanceTo(boss, player);
            var sb = new StringBuilder(256);
            sb.Append("Player distance: ")
              .Append(dist > 0f ? dist.ToString("0.#") + "m" : "unknown").Append('\n');
            sb.Append("Boss HP: ").Append(boss != null ? boss.health.ToString("0") : "?")
              .Append('/').Append(boss != null ? boss.maxHealth.ToString("0") : "?")
              .Append(" — Player HP: ").Append(player != null ? player.health.ToString("0") : "?")
              .Append('/').Append(player != null ? player.maxHealth.ToString("0") : "?").Append('\n');
            sb.Append("Sword: ").Append(string.IsNullOrEmpty(snapshot.swordState) ? "Unknown" : snapshot.swordState)
              .Append(" (boss armed: ").Append(boss != null && boss.weaponEquipped ? "yes" : "no").Append(")\n");
            sb.Append("Health orb available: ")
              .Append(Game.Combat.HealthPickup.AnyActive() ? "yes" : "no");
            if (currentPersona != BossPersona.None)
            {
                sb.Append('\n').Append("Current persona: ").Append(PersonaWord(currentPersona));
                if (heldSeconds > 0f)
                    sb.Append(" (held ").Append(heldSeconds.ToString("0")).Append("s)");
                sb.Append("\nIf the situation has not changed much, switch to a different persona this phase.");
            }
            return sb.ToString();
        }

        // [AI-F8][lead] Display word for a persona — single source of truth
        // shared by the strategist context and the variety guard logs
        // (None shows as Normal, same rules as the HUD).
        public static string PersonaWord(BossPersona persona)
        {
            switch (persona)
            {
                case BossPersona.Distance: return "Distance";
                case BossPersona.Fighter: return "Fighter";
                default: return "Normal";
            }
        }

        // [AI-F8][lead] Deterministic variety rotation for the hold guard in
        // BotController: if the model repeats the same persona for more than
        // maxPersonaHoldSeconds, the next persona is forced around this cycle
        // (Distance -> Fighter -> Normal -> Distance). Pure + public so
        // EditMode tests can assert the cycle. None (no persona yet) stays
        // Normal — the guard never fires on the first ask.
        public static BossPersona NextVarietyPersona(BossPersona current)
        {
            switch (current)
            {
                case BossPersona.Distance: return BossPersona.Fighter;
                case BossPersona.Fighter: return BossPersona.Normal;
                case BossPersona.None: return BossPersona.Normal;
                default: return BossPersona.Distance; // Normal (and anything else)
            }
        }

        // [F52] Case-insensitive contains-parse; the EARLIEST persona word in
        // the reply wins (a verbose answer naming two personas is ambiguous —
        // the first mention is the choice). Anything else -> Normal.
        public static BossPersona ParsePersona(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return BossPersona.Normal;
            string s = raw.ToLowerInvariant();
            int d = s.IndexOf("distance", StringComparison.Ordinal);
            int f = s.IndexOf("fighter", StringComparison.Ordinal);
            int n = s.IndexOf("normal", StringComparison.Ordinal);
            int best = int.MaxValue;
            BossPersona hit = BossPersona.Normal;
            if (d >= 0 && d < best) { best = d; hit = BossPersona.Distance; }
            if (f >= 0 && f < best) { best = f; hit = BossPersona.Fighter; }
            if (n >= 0 && n < best) { best = n; hit = BossPersona.Normal; }
            return hit;
        }

        private static float DistanceTo(EntityState a, EntityState b)
        {
            if (a == null || b == null || a.position == null || b.position == null) return 0f;
            float dx = a.position[0] - b.position[0];
            float dy = a.position[1] - b.position[1];
            float dz = a.position[2] - b.position[2];
            return UnityEngine.Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public async Task<LlmDecision> DecideDetailedAsync(CombatStateSnapshot snapshot,
            ActionManifest manifest, LlmConfig config, ILlmTransport transport = null)
        {
            // [F52] Call counter so tests can assert ForcedHeuristic makes
            // ZERO detailed calls.
            DetailedCallCount++;
            if (snapshot == null || manifest == null || config == null) return default;
            if (string.IsNullOrEmpty(config.EndpointUrl)) return default;
            var client = new LlmClient(config, transport);
            string prompt = BuildSystemPrompt(snapshot, manifest);
            // [F38] Context diet metric: exact request-body bytes (same
            // BuildPayload the transport sends) + decision latency, one log per
            // decision so the next playtest can correlate context size vs
            // latency. Temporary until F38 is validated with data.
            int payloadBytes = 0;
            try
            {
                payloadBytes = System.Text.Encoding.UTF8.GetByteCount(client.BuildPayload(snapshot, prompt));
                LastPayloadBytes = payloadBytes; // [F38][lead] exposed so BotController persists it into the jsonl on real LLM decisions
            }
            catch (System.Exception) { /* the metric must never break a decision */ }
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            LlmResult result;
            try
            {
                result = await client.SendCombatStateAsync(snapshot, prompt);
            }
            catch (System.Exception)
            {
                stopwatch.Stop();
                UnityEngine.Debug.Log("[LlmBrain] payload=" + payloadBytes
                    + " bytes, latency=" + stopwatch.ElapsedMilliseconds + "ms (transport exception)");
                return default;
            }
            stopwatch.Stop();
            UnityEngine.Debug.Log("[LlmBrain] payload=" + payloadBytes
                + " bytes, latency=" + stopwatch.ElapsedMilliseconds + "ms");
            string raw = result != null ? result.RawResponse : null;
            if (result == null || !result.Success || result.Plan == null)
            {
                // [lead diag] Surface the REAL transport error into the fallback warning —
                // "transport failed / empty response" hid req.error (HTTP policy? server down? 404?).
                if (string.IsNullOrEmpty(raw) && result != null && !string.IsNullOrEmpty(result.Error))
                    raw = "[transport] " + result.Error;
                return new LlmDecision(null, raw);
            }
            return new LlmDecision(ValidatePlan(result.Plan, manifest), raw);
        }

        // Keeps only manifest-known actions (canonical ids), max 6. Null if empty.
        public static string ValidatePlan(LlmPlanResponse plan, ActionManifest manifest)
        {
            if (plan == null || plan.actions == null || manifest == null) return null;
            var sb = new StringBuilder("{\"actions\":[");
            int kept = 0;
            foreach (var a in plan.actions)
            {
                if (kept >= LlmPlanResponse.MaxActions) break;
                if (a == null || string.IsNullOrWhiteSpace(a.type)) continue;
                var def = manifest.FindByAlias(a.type.Trim());
                if (def == null)
                {
                    UnityEngine.Debug.LogWarning("[LlmBrain] Discarding unknown action '" + a.type + "'.");
                    continue;
                }
                if (kept > 0) sb.Append(',');
                sb.Append("{\"type\":\"").Append(def.ActionId).Append('\"');
                if (def.NeedsTarget && !string.IsNullOrWhiteSpace(a.target))
                    sb.Append(",\"target\":\"").Append(a.target.Trim()).Append('\"');
                sb.Append('}');
                kept++;
            }
            sb.Append("]}");
            return kept == 0 ? null : sb.ToString();
        }

        // [F35] Tactical context per action so the model decides with real
        // data. [F38] Lines shortened to one clause each — the tactical
        // meaning (damage class, windup, when-to-use) survives, the filler
        // does not. Damage-per-action is NOT exposed by ActionDefinition
        // (lives in CombatDirector), so the canonical lines carry it.
        private static readonly System.Collections.Generic.Dictionary<string, string> ActionTactics =
            new System.Collections.Generic.Dictionary<string, string>
            {
                { "MutantPunch", "golpe rápido corto, daño bajo-medio. Para player pegado." },
                { "BossHeavyAttack", "golpe pesado, windup largo (telegraph rojo), daño alto. Riesgo si el player esquiva." },
                { "BossDodge", "esquiva/retroceso: evita daño un instante, no causa daño." },
                { "Run", "cerrar distancia antes de golpear." },
                // [F46] Ranged cast — canonical line, [F49] balance: 12m, [BAL-F1] 3s beam / 4.5s cast.
                { "BossLaserBeam", "sustained 3s straight laser beam, 12m range, 4.5s cast (1.5s telegraphed wind-up); use when the player is 6-12m away and you can face them; you cannot move while casting." },
                // [F47] Spinning variant — canonical line, [F49] balance: 12m + (2,6] window, [BAL-F1] 2x360° over 4s.
                { "BossLaserTornado", "spinning laser (two full 360° turns over 4s), 12m range, 5.5s cast (telegraphed wind-up); use when the player circles you at 2-6m; you cannot move while casting." },
            };

        public static string BuildSystemPrompt(CombatStateSnapshot snapshot, ActionManifest manifest)
        {
            var sb = new StringBuilder(1024);
            // [Latency fix] qwen3's thinking mode adds 10-15s per decision (measured:
            // 16.2s with think vs 3.7s with /no_think), blowing past the 12s LlmConfig
            // timeout -> 3x retry bursts -> frozen boss. /no_think is qwen3's official
            // soft-switch for the fast path. MUST stay at the start of the prompt.
            sb.Append("You are the Mutant boss in a tactical duel. /no_think ");
            sb.Append("Your usable actions (use ONLY these ids):");
            foreach (var def in manifest.Actions)
            {
                if (def == null) continue;
                sb.Append("\n- ");
                sb.Append(def.ActionId);
                // [F35] Range/damage-type from the ActionDefinition (range only
                // if > 0: for moves like Run it is noise). [F38] "(2m)" not "(rango 2m)".
                if (def.Range > 0f) sb.Append(" (").Append(def.Range.ToString("0.#")).Append("m");
                if (def.IsHeavy) sb.Append(", heavy");
                if (def.Range > 0f) sb.Append(')');
                string tactics;
                if (ActionTactics.TryGetValue(def.ActionId, out tactics))
                    sb.Append(": ").Append(tactics);
                if (def.RoleRestriction == ActionRole.BossOnly) sb.Append(" [boss-only]");
            }
            // [F43] Predator bias (user playtest: "el boss se sentía pasivo"): the
            // LLM otherwise returns timid plans (idle/block loops) while the
            // grunts attack nonstop. Two sentences only — F38 diet respected.
            sb.Append("\nYou are an aggressive apex predator: prefer attack chains (Run into range then 2-3 attacks), keep pressing the offensive, only dodge/block when the player is winding up a heavy attack or you are below 30% health. NEVER output Idle while the player is within 8m. ");
            // [F45] Enrage directive. NOTE: BuildSystemPrompt has no snapshot
            // access, so the line is unconditional — when NOT furious the boss
            // simply reads it as a stance hint; when furious the wire's
            // "furious":true (CombatStateSerializer) confirms it.
            sb.Append("\nWhen you are ENRAGED (furious), attack relentlessly with Heavy attacks and never retreat. While ENRAGED, strongly prefer BossLaserTornado over BossLaserBeam. ");
            sb.Append("\nDodge alternative: BossDodge (Front Twist Flip). ");
            sb.Append("If the player isAttacking, consider evading with BossDodge. ");
            sb.Append("Respond with EXACTLY {\"actions\":[{\"type\":\"<id>\",\"target\":\"left|center|right\"}]} ");
            sb.Append("with 1 to 6 actions, target only for actions that need one. ");
            sb.Append("Reply with ONLY that JSON, no extra text.");
            return sb.ToString();
        }
    }
}
