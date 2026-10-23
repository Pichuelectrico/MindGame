using System;
using System.Collections.Generic;
using Game.Actions;

namespace Game.AI
{
    // [F52] Strategist personas picked by the SECOND LLM prompt (the
    // strategist). None = classic behavior; Normal = the strategist's
    // explicit balanced answer — same rules as None; Distance kites;
    // Fighter brawls.
    public enum BossPersona { None, Distance, Fighter, Normal }

    // Heuristic fallback brain for the boss NPC (used when the LLM fails/slow).
    // Deterministic GIVEN the same Rng state: same snapshot + same seed ->
    // same plan JSON. String seam like PlanParser: output is canonical
    // {"actions":[...]} with manifest actionIds.
    public sealed class BotBrain
    {
        private const float ThreatCloseRange = 4f;

        // [F8] Anti-mirror factor: after a Block* decision, the next defensive
        // roll shrinks the block share by this much (two blockers facing each
        // other stall the fight — user playtest: "se pasan en una pelea de
        // bloqueos que no es divertido").
        private const float AntiMirrorBlockFactor = 0.25f;

        // [F11.1] Throwing the sword is an EMERGENCY ranged resource, not a
        // routine option (user playtest: "que priorize usar la espada que
        // lanzarla... muy baja elección automática por la pérdida del arma").
        // When mid-range with the weapon, the brain throws only with this
        // probability per decision tick — otherwise it closes distance or
        // defends. 1.0 restores the old deterministic behavior (tests).
        public float ThrowChance = 0.05f;

        // [F8] Close-range defensive weighting (user playtest: NPCs blocked
        // too much). Applied ONLY in the defensive band (threat close, no
        // usable melee): the attack share means "press forward" (Run/Walk),
        // dodge is the medium slice and block the smallest. Pure class, so
        // these are plain fields with defaults — the wire/inspector layer can
        // override them in code (BotController holds the brain instance).
        public struct DefensiveWeights
        {
            public float AttackWeight;
            public float DodgeWeight;
            public float BlockWeight;

            public static DefensiveWeights Default => new DefensiveWeights
            {
                AttackWeight = 0.65f,
                DodgeWeight = 0.20f,
                BlockWeight = 0.15f
            };
        }

        // Last melee attack chosen (variety: never repeat twice in a row when
        // alternatives exist). Instance state; BotController holds one instance.
        private string lastAttackId;

        // [F8] True right after a Block* defensive decision (anti-mirror bias).
        private bool lastDefensiveWasBlock;

        // [F8] Defensive weighting (see DefensiveWeights). Defaults keep attack
        // dominant so fights advance; the wire layer may override per-NPC.
        public DefensiveWeights Weights = DefensiveWeights.Default;

        // [F8] Randomness source for the weighted defensive roll. Runtime:
        // time-seeded (variety the user asked for). Tests: inject a seeded
        // Random for deterministic distributions.
        public Random Rng = new Random();

        // [F45] Modo Furia: when true, BossChain re-chains the boss specials
        // (Heavy chains normally) with heavies first. Defaults to false so every
        // existing Decide(...) call site keeps its exact behavior.
        public bool Furious { get; set; }

        // [F46] Optional per-action cooldown probe, injected by BotController
        // (RunHeuristic). null = nothing is ever on cooldown (legacy behavior).
        // The laser rule consults it before picking BossLaserBeam; tests inject
        // a lambda for deterministic gating. Pure function of the action id.
        public Func<string, float> RemainingCooldownOf;

        // [F51] Boss seeks health orbs at <= this HP fraction. Controller
        // passes its serialized knob each decision; default 0.5 standalone.
        // NEVER consulted while Furious (predator relentless).
        public float SeekHealthThreshold = 0.5f;

        // [F52] Active strategist persona — BotController injects it ONLY in
        // Estratega mode (Llm/ForcedHeuristic keep None = classic rules).
        // None = classic behavior, every rule below unchanged; Normal = the
        // strategist's explicit "balanced" answer, identical rules to None;
        // Distance kites (never Run-approaches the player); Fighter brawls
        // (sword-first + deterministic throw). Pure data, deterministic —
        // the persona branches use no Rng.
        public BossPersona Persona { get; set; }

        // [F52] Distance persona: escape when the player is closer than this
        // (Retreat, dodge-spam fallback). Furia drops the escape (relentless)
        // but keeps the tornado priority and never Run-approaches.
        private const float DistanceEscapeDist = 5f;

        // [F52] Fighter persona: armed beyond melee reach but inside
        // ThrowWeapon.Range -> deterministic throw. The persona OWNS the
        // throw; the [F11.1] 5% gamble only runs for non-Fighter brains.
        private const float FighterThrowMinDist = 6f;

        // [F46] Ranged cast only fires OUTSIDE melee pressure: closer than this
        // the boss always prefers its melee chain, never the laser.
        private const float LaserCastMinDist = 6f;

        // [F47] Tornado window, [F49] balance: mid-range punish for a player
        // circling the boss — (2, 6] normal, (2, 9] in FURIA (covers almost
        // everything except pure melee; the straight laser becomes the
        // fallback past 6m when the tornado is unavailable).
        private const float TornadoMinDist = 2f;
        private const float TornadoMaxDist = 6f;
        private const float TornadoFuryMaxDist = 9f;

        // Melee catalogs in preference order. [F15.1] WEAPON-AWARE: an armed
        // fighter leads with AttackSword (user: "que el golpe cuando tienes la
        // espada sea la animación de sword and shield attack" — AttackKick
        // outranked AttackSword in the old flat list, so armed fighters kept
        // kicking with the sword in hand). Unarmed catalog never contains the
        // weapon-gated AttackSword (requiresWeapon: 1 enforces it too).
        private static readonly string[] ArmedMeleePriority =
        {
            "AttackSword", "AttackCombo", "AttackKick", "Punch"
        };

        private static readonly string[] UnarmedMeleePriority =
        {
            "AttackKick", "AttackCombo", "Punch"
        };

        private static string[] MeleeCatalogFor(bool hasWeapon) =>
            hasWeapon ? ArmedMeleePriority : UnarmedMeleePriority;

        // [F8] Boss signature attacks ride at the HEAD of the melee catalog for
        // bosses only: an armed boss still leads with its heavy attack, and an
        // unarmed boss keeps MutantPunch. They stay behind their isBoss gate in
        // PickMelee (belt and suspenders — non-boss catalogs never see them) and
        // behind IsUsable, so stamina/range still rule (weak unarmed boss ->
        // Punch, not a stalled heavy swing).
        private static readonly string[] BossMeleeSpecials =
        {
            "BossHeavyAttack", "MutantPunch"
        };

        private static IEnumerable<string> MeleeCatalogFor(bool hasWeapon, bool isBoss)
        {
            if (isBoss)
                foreach (var id in BossMeleeSpecials) yield return id;
            foreach (var id in MeleeCatalogFor(hasWeapon)) yield return id;
        }

        public string Decide(CombatStateSnapshot snapshot, ActionManifest manifest, string selfId = null)
        {
            string safe = "{\"actions\":[{\"type\":\"IdleFight\"}]}";
            if (snapshot == null || manifest == null) return safe;

            var self = PickSelf(snapshot, selfId);
            if (self == null) return WithType(manifest, safe, "IdleFight");
            var threat = PickThreat(snapshot, self);
            if (threat == null) return WithType(manifest, safe, "IdleFight");

            float dist = DistanceBetween(snapshot, self, threat);
            bool hasWeapon = self.weaponEquipped;
            ActionRole role = self.isBoss ? ActionRole.BossOnly : ActionRole.PlayerOnly;
            float stamina = self.stamina;

            // 0. Reactivity: player mid-attack and boss can twist away -> BossDodge.
            if (self.isBoss && threat.isAttacking)
            {
                var evade = manifest.Find("BossDodge");
                if (IsUsable(evade, hasWeapon, role, stamina))
                    return Single(evade.ActionId, null);
            }

            // 0b. [F52] Fighter persona: unarmed + sword in map -> the sword
            // comes FIRST (a brawler wants the weapon before brawling).
            // Elevated ABOVE the melee chain — the classic rule 2b (below)
            // stays in place for non-Fighter brains. Deterministic.
            if (self.isBoss && Persona == BossPersona.Fighter && !hasWeapon
                && snapshot.swordState == "InMap")
            {
                var runToSword = manifest.Find("Run");
                if (IsUsable(runToSword, hasWeapon, role, stamina))
                    return Single(runToSword.ActionId, threat.id);
            }

            // 0c. [F52] Distance persona (Estratega): the kiting boss. It
            // owns the situation whenever it has an opinion; null falls
            // through to the classic rules with two adjustments below (the
            // defensive approach and the far Run are suppressed — a Distance
            // boss NEVER Run-approaches the player). The F51 orb rule runs
            // first here too: personas respect looting the same way. Furia
            // keeps cadence/speed/tornado-priority (controller + expanded
            // window) and drops the escape, but never approaches.
            if (self.isBoss && Persona == BossPersona.Distance)
            {
                var kite = TrySeekOrb(self, manifest, hasWeapon, role, stamina)
                           ?? DecideDistanceKite(manifest, hasWeapon, role, stamina, dist);
                if (kite != null) return kite;
            }

            // 1. Melee kill range. [F43] The boss CHAINS (special + light hits,
            // 2-3 actions) instead of swinging once — a single swing reads as
            // passive against grunts that attack nonstop. Follow-ups are
            // deterministic (no Rng) so identical snapshots still yield
            // identical plans (Deterministic contract).
            var melee = PickMelee(self, manifest, hasWeapon, role, stamina, dist);
            if (melee != null)
            {
                if (self.isBoss)
                    return Chain(BossChain(manifest, hasWeapon, role, stamina, dist, melee), threat.id);
                return Single(melee.ActionId, threat.id);
            }

            // 1b. [F52] Fighter persona: armed beyond melee reach but inside
            // ThrowWeapon.Range -> deterministic throw (the persona OWNS the
            // throw). Furia keeps this bias — cadence/speed are controller-
            // side and the melee chain above already runs first in range.
            var throwW = manifest.Find("ThrowWeapon");
            if (self.isBoss && Persona == BossPersona.Fighter && hasWeapon
                && IsUsable(throwW, true, role, stamina)
                && dist > FighterThrowMinDist && dist <= throwW.Range)
                return Single(throwW.ActionId, threat.id);

            // 2. Mid range with weapon: throw only as a RARE ranged resource
            // [F11.1] — default 5% per tick; the default path is closing
            // distance (step 3/4) or defending. Losing the weapon is costly,
            // so auto-selection must be a last-resort gamble, never routine.
            if (hasWeapon && IsUsable(throwW, true, role, stamina)
                && dist <= throwW.Range
                && (Rng != null ? Rng.NextDouble() : 0.0) < ThrowChance)
                return Single(throwW.ActionId, threat.id);

            // 2b. Unarmed and the sword is up for grabs: run (the controller
            // steers toward it). Deterministic rule instead of a config chance.
            if (!hasWeapon && snapshot.swordState == "InMap")
            {
                var runToSword = manifest.Find("Run");
                if (IsUsable(runToSword, hasWeapon, role, stamina))
                    return Single(runToSword.ActionId, threat.id);
            }

            // 2c. [F51] Seek health (extracted into TrySeekOrb so the [F52]
            // personas share the exact same rule — the strategist personas
            // respect looting). Placed after melee/punish and sword recovery
            // so close combat and re-arming keep priority; before the lasers
            // so looting beats ranged posturing. Deterministic — no Rng.
            var seekPlan = TrySeekOrb(self, manifest, hasWeapon, role, stamina);
            if (seekPlan != null) return seekPlan;

            // 2d. [F47] Boss mid-range tornado, [F49] windows: (2,6] normal,
            // (2,9] en FURIA — the tornado DOMINATES mid-range when enraged
            // and the straight laser becomes its fallback. Defined, affordable
            // (stamina >= 35 via cost) and OFF cooldown -> spin. Evaluated
            // BEFORE the straight laser so it owns its window. Deterministic —
            // no Rng.
            var tornado = manifest.Find("BossLaserTornado");
            float tornadoMax = Furious ? TornadoFuryMaxDist : TornadoMaxDist;
            if (self.isBoss && dist > TornadoMinDist && dist <= tornadoMax
                && IsUsable(tornado, hasWeapon, role, stamina)
                && (RemainingCooldownOf == null || RemainingCooldownOf(tornado.ActionId) <= 0f))
                return Single(tornado.ActionId, threat.id);

            // 2e. [F46] Boss ranged pressure: far from the threat, the laser
            // defined, affordable (stamina >= 30 via cost) and OFF cooldown ->
            // cast BossLaserBeam INSTEAD of just running in. Deterministic —
            // no Rng. In melee range step 1 already returned, so the laser is
            // never picked close; the controller's per-action cooldown (default
            // 10s) keeps it a resource, not a spam. [F52] The Distance persona
            // also respects the def's actual reach (<= 12m) — it never wastes
            // a cast the beam cannot complete.
            var laser = manifest.Find("BossLaserBeam");
            if (self.isBoss && dist > LaserCastMinDist
                && IsUsable(laser, hasWeapon, role, stamina)
                && (Persona != BossPersona.Distance || dist <= laser.Range)
                && (RemainingCooldownOf == null || RemainingCooldownOf(laser.ActionId) <= 0f))
                return Single(laser.ActionId, threat.id);

            // 3. Close-range band (no usable melee reaches here): WEIGHTED
            // defensive pick. Attack share = press forward (Run/Walk), dodge
            // medium, block smallest; a Block* decision shrinks the next block
            // share (anti-mirror). Shares renormalize over usable candidates.
            // [F52] The Distance persona never presses forward — its approach
            // share is dropped and the roll runs over defense-only shares.
            if (dist <= ThreatCloseRange)
            {
                var defensive = PickDefensive(manifest, hasWeapon, role, stamina, threat.id,
                    suppressApproach: Persona == BossPersona.Distance);
                if (defensive != null) return defensive;
            }

            // 4. Far: close the distance. [F52] Distance persona NEVER
            // Run-approaches the player — it holds ground (IdleFight) until a
            // laser or the tornado comes off cooldown.
            if (Persona != BossPersona.Distance)
            {
                var run = manifest.Find("Run");
                if (IsUsable(run, hasWeapon, role, stamina)) return Single(run.ActionId, threat.id);
                var walk = manifest.Find("Walk");
                if (IsUsable(walk, hasWeapon, role, stamina)) return Single(walk.ActionId, threat.id);
            }

            return WithType(manifest, safe, "IdleFight");
        }

        // [F51] Shared orb rule — the classic 2c step AND the [F52] Distance
        // persona consult this. Deterministic, no Rng; never while Furious.
        // Pure algorithmic decision: the LLM never sees orbs (they are not
        // snapshot entities) — the strategist personas respect it by running
        // it BEFORE their own overrides (see Decide rule 0c).
        private string TrySeekOrb(EntityState self,
            ActionManifest manifest, bool hasWeapon, ActionRole role, float stamina)
        {
            if (self == null || !self.isBoss || Furious) return null;
            if (self.maxHealth <= 0f
                || self.health / self.maxHealth > SeekHealthThreshold) return null;
            if (!Game.Combat.HealthPickup.AnyActive()) return null;
            var orb = Game.Combat.HealthPickup.NearestTo(new UnityEngine.Vector3(
                self.position[0], self.position[1], self.position[2]));
            if (orb == null) return null;
            var orbRun = manifest.Find("Run");
            return IsUsable(orbRun, hasWeapon, role, stamina)
                ? Single(orbRun.ActionId, orb.gameObject.name)
                : null;
        }

        // [F52] Distance kite rules (deterministic, no Rng). Order: escape
        // when pressured, then the tornado (FIRST so furia keeps its expanded
        // (2,9] priority window), then the straight laser across its whole
        // cast window (6m .. def range 12m) whenever off cooldown. Null when
        // the persona has no opinion this tick — classic rules take over
        // (minus the approach: rules 3/4 are Distance-gated).
        private string DecideDistanceKite(ActionManifest manifest, bool hasWeapon,
            ActionRole role, float stamina, float dist)
        {
            if (!Furious && dist < DistanceEscapeDist)
            {
                var retreat = manifest.Find("Retreat");
                if (IsUsable(retreat, hasWeapon, role, stamina))
                    return Single(retreat.ActionId, null);
                var evade = manifest.Find("BossDodge");
                if (IsUsable(evade, hasWeapon, role, stamina))
                    return Single(evade.ActionId, null); // dodge-spam escape
            }
            var tornado = manifest.Find("BossLaserTornado");
            float tornadoMax = Furious ? TornadoFuryMaxDist : TornadoMaxDist;
            if (dist > TornadoMinDist && dist <= tornadoMax
                && IsUsable(tornado, hasWeapon, role, stamina)
                && (RemainingCooldownOf == null || RemainingCooldownOf(tornado.ActionId) <= 0f))
                return Single(tornado.ActionId, null);
            var laser = manifest.Find("BossLaserBeam");
            if (IsUsable(laser, hasWeapon, role, stamina)
                && dist > LaserCastMinDist && dist <= laser.Range
                && (RemainingCooldownOf == null || RemainingCooldownOf(laser.ActionId) <= 0f))
                return Single(laser.ActionId, null);
            return null;
        }

        private ActionDefinition PickMelee(EntityState self, ActionManifest manifest,
            bool hasWeapon, ActionRole role, float stamina, float dist)
        {
            ActionDefinition specialFallback = null;
            if (self.isBoss)
            {
                foreach (var id in BossMeleeSpecials)
                {
                    var def = manifest.Find(id);
                    if (!IsUsable(def, hasWeapon, role, stamina) || dist > def.Range) continue;
                    if (!string.Equals(def.ActionId, lastAttackId, StringComparison.OrdinalIgnoreCase))
                    {
                        lastAttackId = def.ActionId;
                        return def;
                    }
                    if (specialFallback == null) specialFallback = def;
                }
            }

            // [F18] Weighted variety roll over the weapon-aware catalog
            // (playtest: the same attack every tick got monotonous). Shares:
            // armed -> AttackSword 50 / AttackKick 25 / Punch 25; unarmed ->
            // AttackKick 40 / Punch 40 / AttackCombo 20. Ids without a share
            // (AttackCombo while armed) stay usable as deterministic fallbacks
            // but never win a roll while a weighted option exists. Anti-repeat:
            // the last pick loses its share when another candidate is usable.
            var candidates = new List<ActionDefinition>();
            var shares = new List<double>();
            foreach (var id in MeleeCatalogFor(hasWeapon, self.isBoss))
            {
                if (id == "BossHeavyAttack" || id == "MutantPunch") continue; // handled above
                var def = manifest.Find(id);
                // hasWeapon is respected for every id — an unarmed fighter must
                // never plan AttackSword (the sword-fetch rule runs a plan of
                // Run toward it instead; planning AttackSword unarmed made the
                // executor substitute it and broke the close-range punch case).
                if (!IsUsable(def, hasWeapon, role, stamina) || dist > def.Range) continue;
                candidates.Add(def);
                shares.Add(MeleeShare(def.ActionId, hasWeapon));
            }
            if (candidates.Count == 0) return specialFallback;

            double total = 0;
            for (int i = 0; i < shares.Count; i++) total += shares[i];
            if (total > 0)
            {
                // Anti-repeat: when another candidate exists, the last pick
                // loses its share and the rest renormalize over the roll.
                if (candidates.Count > 1)
                {
                    for (int i = candidates.Count - 1; i >= 0; i--)
                    {
                        if (!string.Equals(candidates[i].ActionId, lastAttackId, StringComparison.OrdinalIgnoreCase)) continue;
                        candidates.RemoveAt(i);
                        shares.RemoveAt(i);
                    }
                    total = 0;
                    for (int i = 0; i < shares.Count; i++) total += shares[i];
                }
                if (total > 0)
                {
                    double pick = (Rng != null ? Rng.NextDouble() : 0.0) * total;
                    double acc = 0;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        acc += shares[i];
                        if (pick < acc)
                        {
                            lastAttackId = candidates[i].ActionId;
                            return candidates[i];
                        }
                    }
                    lastAttackId = candidates[candidates.Count - 1].ActionId;
                    return candidates[candidates.Count - 1];
                }
            }

            // Deterministic fallback (all shares zero, e.g. only AttackCombo
            // usable while armed): same behavior as pre-F18 — first usable.
            var fallback = candidates[0];
            lastAttackId = fallback.ActionId;
            return fallback;
        }

        // Configured share of a melee id in the variety roll (0 = fallback-only).
        private static double MeleeShare(string actionId, bool hasWeapon)
        {
            if (!hasWeapon)
            {
                switch (actionId)
                {
                    case "AttackKick": return 0.40;
                    case "Punch": return 0.40;
                    case "AttackCombo": return 0.20;
                }
            }
            else
            {
                switch (actionId)
                {
                    case "AttackSword": return 0.50;
                    case "AttackKick": return 0.25;
                    case "Punch": return 0.25;
                }
            }
            return 0;
        }

        // [F43] Aggressive boss chain: the special (or weighted pick) followed
        // by light hits from the weapon-aware catalog — up to 3 actions, capped
        // by cumulative stamina so the executor never queues doomed steps.
        // Deterministic: no Rng here, identical state -> identical chain.
        // [F45] En furia the specials are re-chainable ("encadenar Heavy
        // normal") and take priority: two deterministic passes (specials first,
        // then lights) — no sort, no Rng, identical state still yields
        // identical chains.
        private List<ActionDefinition> BossChain(ActionManifest manifest, bool hasWeapon,
            ActionRole role, float stamina, float dist, ActionDefinition first)
        {
            var chain = new List<ActionDefinition> { first };
            float spent = first.StaminaCost;
            for (int pass = 0; pass < (Furious ? 2 : 1) && chain.Count < 3; pass++)
            {
                // Pass 0: non-fury = light catalog only; fury = specials again
                // (heavy re-chains first). Pass 1 (fury only): the lights.
                bool passTakesSpecials = Furious && pass == 0;
                foreach (var id in MeleeCatalogFor(hasWeapon, true))
                {
                    if (chain.Count >= 3) break;
                    bool isSpecial = id == "BossHeavyAttack" || id == "MutantPunch";
                    if (isSpecial != passTakesSpecials) continue;
                    var def = manifest.Find(id);
                    if (!IsUsable(def, hasWeapon, role, stamina) || dist > def.Range) continue;
                    if (spent + def.StaminaCost > stamina) continue;
                    chain.Add(def);
                    spent += def.StaminaCost;
                }
            }
            return chain;
        }

        private static string Chain(List<ActionDefinition> defs, string targetId)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"actions\":[");
            for (int i = 0; i < defs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"type\":\"").Append(defs[i].ActionId).Append('"');
                if (!string.IsNullOrEmpty(targetId))
                    sb.Append(",\"target\":\"").Append(targetId).Append('"');
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // [F8] Weighted pick inside the close-range defensive band. Candidates:
        // attack share = press forward with Run (or Walk) — ONLY when an attack
        // is affordable (closing distance is useless if you can't swing when
        // you get there); dodge; block. Each
        // share only counts when its action is usable; shares renormalize so
        // the realized probabilities match the configured weights. A Block*
        // decision arms the anti-mirror bias for the next roll.
        // [F52] suppressApproach (Distance persona) drops the attack share —
        // a kiting boss never presses forward in the defensive band.
        private string PickDefensive(ActionManifest manifest, bool hasWeapon, ActionRole role, float stamina, string threatId,
            bool suppressApproach = false)
        {
            var block = manifest.Find("BlockCenter");
            var dodge = manifest.Find("Dodge");
            var run = manifest.Find("Run");
            var walk = manifest.Find("Walk");

            bool blockUsable = IsUsable(block, hasWeapon, role, stamina);
            bool dodgeUsable = IsUsable(dodge, hasWeapon, role, stamina);
            // Approach (Run/Walk) shares the band ONLY when an attack is
            // affordable: closing distance is pointless if the fighter can't
            // swing once it gets there (F3 intent: low stamina + close rival
            // -> DEFEND). Range is irrelevant here — step 3 only runs when no
            // usable melee is already in range — but stamina decides whether
            // pressing forward has any value at all.
            bool attackAffordable = false;
            foreach (var id in MeleeCatalogFor(hasWeapon, role == ActionRole.BossOnly))
            {
                if (IsUsable(manifest.Find(id), hasWeapon, role, stamina))
                {
                    attackAffordable = true;
                    break;
                }
            }
            var approach = !suppressApproach && attackAffordable
                ? (IsUsable(run, hasWeapon, role, stamina) ? run
                   : IsUsable(walk, hasWeapon, role, stamina) ? walk
                   : null)
                : null;

            // [F43] Aggressive boss: with any affordable attack the interim
            // brain ALWAYS presses forward (no Idle/Block by default). Survival
            // dodges come from the controller's reactive polls (F28/F31), not a
            // timid plan; when nothing is affordable the roll below keeps the
            // LowStamina contract (block) alive.
            if (role == ActionRole.BossOnly && approach != null)
            {
                lastDefensiveWasBlock = false;
                return Single(approach.ActionId, threatId);
            }

            float blockShare = Weights.BlockWeight;
            if (lastDefensiveWasBlock) blockShare *= AntiMirrorBlockFactor;

            double attackShare = approach != null ? Weights.AttackWeight : 0f;
            double dodgeShare = dodgeUsable ? Weights.DodgeWeight : 0f;
            double blockShareW = blockUsable ? blockShare : 0f;
            double total = attackShare + dodgeShare + blockShareW;
            if (total <= 0f) return null; // nothing usable here -> step 4/IdleFight

            double pick = Rng.NextDouble() * total;
            if (pick < attackShare)
            {
                lastDefensiveWasBlock = false;
                return Single(approach.ActionId, threatId);
            }
            if (pick < attackShare + dodgeShare && dodgeUsable)
            {
                lastDefensiveWasBlock = false;
                return Single(dodge.ActionId, null);
            }
            if (blockUsable)
            {
                lastDefensiveWasBlock = true;
                return Single(block.ActionId, null);
            }
            return null; // unreachable in practice; keeps the compiler honest
        }

        private static bool IsUsable(ActionDefinition def, bool hasWeapon, ActionRole role, float stamina)
        {
            return def != null && def.IsAvailable(hasWeapon, role, stamina);
        }

        private static EntityState PickSelf(CombatStateSnapshot s, string selfId)
        {
            if (s.entities == null || s.entities.Length == 0) return null;
            if (!string.IsNullOrEmpty(selfId))
            {
                foreach (var e in s.entities)
                    if (e != null && e.id == selfId) return e;
            }
            foreach (var e in s.entities)
                if (e != null && e.isBoss) return e;
            return s.entities[0];
        }

        private static EntityState PickThreat(CombatStateSnapshot s, EntityState self)
        {
            EntityState best = null;
            float bestDist = float.MaxValue;
            foreach (var e in s.entities)
            {
                if (e == null || e == self || !e.isTargetable) continue;
                float d = DistanceBetween(s, self, e);
                if (d < bestDist) { bestDist = d; best = e; }
            }
            return best;
        }

        private static float DistanceBetween(CombatStateSnapshot s, EntityState a, EntityState b)
        {
            if (s.distances != null)
            {
                foreach (var d in s.distances)
                {
                    if (d == null) continue;
                    if ((d.fromId == a.id && d.toId == b.id) || (d.fromId == b.id && d.toId == a.id))
                        return d.distance;
                }
            }
            if (a.position == null || b.position == null
                || a.position.Length < 3 || b.position.Length < 3)
                return float.MaxValue;
            float dx = a.position[0] - b.position[0];
            float dy = a.position[1] - b.position[1];
            float dz = a.position[2] - b.position[2];
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static string Single(string type, string target)
        {
            if (string.IsNullOrEmpty(target)) return "{\"actions\":[{\"type\":\"" + type + "\"}]}";
            return "{\"actions\":[{\"type\":\"" + type + "\",\"target\":\"" + target + "\"}]}";
        }

        private static string WithType(ActionManifest manifest, string fallback, string preferred)
        {
            var def = manifest.Find(preferred) ?? manifest.Find("Idle");
            if (def != null) return Single(def.ActionId, null);
            return fallback;
        }
    }
}
