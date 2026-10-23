using System;
using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.Execution
{
    public enum WheelIntent { None, GoPickupWeapon, GoTakeHealth, MeleeNearest }

    // Runs wheel intents on the player: exit Planning, then steer + chain Run
    // plans until the goal is met, timeout hits, or damage cancels. Damage
    // cancels via HP-drop detection (the reaction owns the fighter then).
    [DisallowMultipleComponent]
    public sealed class WheelIntentRunner : MonoBehaviour
    {
        private const string RunPlanJson = "{\"actions\":[{\"type\":\"Run\"}]}";

        [SerializeField] private ActionExecutor executor;
        [SerializeField] private HealthSystem health;
        [SerializeField] private Game.Actions.ActionManifest manifest;
        [SerializeField] private float reachRadius = 1.2f;
        [SerializeField] private float turnSpeedDegPerSec = 360f;
        [SerializeField] private float timeoutSeconds = 5f;

        public bool HasActiveIntent => active != WheelIntent.None;

        private WheelIntent active = WheelIntent.None;
        private float intentTimer;
        private int hpAtStart;
        private Vector3 goal;
        private bool goalValid;
        private HealthSystem meleeTarget;
        private Game.Actions.ActionDefinition meleeAction;

        // Sole entry point for the wheel. Returns false with a clear log when
        // the intent makes no sense; otherwise exits Planning and starts
        // navigating, returning true.
        public bool RunFromWheel(WheelIntent intent, TimeStateMachine time)
        {
            if (executor == null || health == null)
            {
                Debug.Log("[WheelIntent] No executor/health bound; intent rejected.");
                return false;
            }
            if (health.IsDead)
            {
                Debug.Log("[WheelIntent] Fighter is dead; intent rejected.");
                return false;
            }
            if (intent == WheelIntent.GoPickupWeapon && executor.HasWeapon)
            {
                Debug.Log("[WheelIntent] Already armed; intent rejected.");
                return false;
            }
            if (intent == WheelIntent.GoTakeHealth && !HealthPickup.AnyActive())
            {
                Debug.Log("[WheelIntent] No health pickups in scene; intent rejected.");
                return false;
            }
            if (intent == WheelIntent.MeleeNearest && !ResolveMeleeTarget())
            {
                Debug.Log("[WheelIntent] No living melee target; intent rejected.");
                return false;
            }
            // Exit Planning via CancelPlanning (Planning -> Running), then drive
            // the Run chain directly from Running — the same pattern as
            // PlayerAutoAI. The empty-plan + AcceptPlan trick does NOT work:
            // AcceptPlan calls StartPlan() BEFORE SetPhase(Executing), so an
            // instantly-draining plan fires PlanFinished while the phase is
            // still Planning and OnPlanFinished() drops it -> stuck in
            // Executing forever.
            if (time != null && !time.CancelPlanning() && time.Current != CombatPhase.Running)
            {
                Debug.Log("[WheelIntent] Could not exit Planning; intent rejected.");
                return false;
            }
            active = intent;
            intentTimer = 0f;
            hpAtStart = health.CurrentHealth;
            executor.PlanFinished += OnPlanFinishedRefill;
            OnPlanFinishedRefill(); // start the first Run leg immediately
            Debug.Log("[WheelIntent] Started " + intent + ".");
            return true;
        }

        public void CancelIntent(string reason)
        {
            if (active == WheelIntent.None) return;
            active = WheelIntent.None;
            if (executor != null) executor.PlanFinished -= OnPlanFinishedRefill;
            Debug.Log("[WheelIntent] Cancelled: " + reason);
        }

        private void OnDisable()
        {
            if (executor != null) executor.PlanFinished -= OnPlanFinishedRefill;
            active = WheelIntent.None;
        }

        // No gap for PlayerAutoAI to slip through: refill synchronously.
        private void OnPlanFinishedRefill()
        {
            if (active == WheelIntent.None || executor == null) return;
            if (active == WheelIntent.MeleeNearest) return;
            if (executor.LoadPlan(RunPlanJson)) executor.StartPlan();
        }

        private void Update()
        {
            TickIntent(Time.deltaTime);
        }

        // Public for EditMode tests (Update delegates here).
        public void TickIntent(float dt)
        {
            if (active == WheelIntent.None || executor == null || health == null) return;
            if (health.IsDead) { CancelIntent("died"); return; }
            if (health.CurrentHealth < hpAtStart) { CancelIntent("damage taken, reaction owns fighter"); return; }

            intentTimer += dt;
            if (intentTimer >= timeoutSeconds) { CancelIntent("timeout"); return; }

            if (!ResolveGoal()) { CancelIntent("goal gone"); return; }
            FaceGoal(dt);
            if (CheckSuccess())
            {
                if (active == WheelIntent.MeleeNearest) FinishMelee();
                else FinishIntent();
                return;
            }
            // Within reach: hold still and let the auto-pickup polls fire
            // instead of orbiting the goal.
            Vector3 flat = goal - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude <= reachRadius * reachRadius) return;
            if (!executor.IsRunning)
            {
                if (executor.LoadPlan(RunPlanJson)) executor.StartPlan();
            }
        }

        private bool ResolveGoal()
        {
            goalValid = false;
            if (active == WheelIntent.GoPickupWeapon)
            {
                var sword = FindFirstObjectByType<SwordManager>();
                if (sword == null || sword.State != SwordState.InMap) return false;
                goal = sword.SwordPosition;
                goalValid = true;
                return true;
            }
            if (active == WheelIntent.MeleeNearest)
            {
                if (meleeTarget == null || meleeTarget.IsDead)
                {
                    if (!ResolveMeleeTarget()) return false;
                }
                goal = meleeTarget.transform.position;
                goalValid = true;
                return true;
            }
            var pickup = HealthPickup.NearestTo(transform.position);
            if (pickup == null) return false;
            goal = pickup.transform.position;
            goalValid = true;
            return true;
        }

        private void FaceGoal(float dt)
        {
            if (!goalValid) return;
            Vector3 to = goal - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                Quaternion.LookRotation(to.normalized),
                turnSpeedDegPerSec * dt);
        }

        private bool CheckSuccess()
        {
            if (active == WheelIntent.GoPickupWeapon)
                return executor.HasWeapon;
            if (active == WheelIntent.MeleeNearest)
                return meleeTarget != null && Vector3.Distance(transform.position, meleeTarget.transform.position)
                    <= meleeAction.Range + reachRadius;
            return health.CurrentHealth > hpAtStart;
        }

        private bool ResolveMeleeTarget()
        {
            meleeTarget = null;
            meleeAction = null;
            if (manifest == null) return false;
            string preferred = executor.HasWeapon ? "AttackSword" : "AttackKick";
            meleeAction = manifest.Find(preferred);
            if (meleeAction == null || !meleeAction.IsAvailable(executor.HasWeapon, executor.Role, executor.Stamina))
                meleeAction = manifest.Find("Punch");
            if (meleeAction == null || !meleeAction.IsAvailable(executor.HasWeapon, executor.Role, executor.Stamina)) return false;
            float best = float.MaxValue;
            foreach (var candidate in FindObjectsByType<HealthSystem>(FindObjectsSortMode.None))
            {
                if (candidate == null || candidate == health || candidate.IsDead) continue;
                if (GruntSpawner.IsDormantBoss(candidate.gameObject)) continue;
                float distance = Vector3.Distance(transform.position, candidate.transform.position);
                if (distance < best) { best = distance; meleeTarget = candidate; }
            }
            return meleeTarget != null;
        }

        private void FinishMelee()
        {
            var target = meleeTarget;
            var action = meleeAction;
            active = WheelIntent.None;
            executor.PlanFinished -= OnPlanFinishedRefill;
            meleeTarget = null;
            if (target == null || target.IsDead || action == null) return;
            string targetId = target.gameObject.name;
            string json = "{\"actions\":[{\"type\":\"" + action.ActionId
                + "\",\"target\":\"" + targetId + "\"}]}";
            if (executor.LoadPlan(json)) executor.StartPlan();
            else Debug.Log("[WheelIntent] Melee target became unavailable; attack cancelled.");
        }

        private void FinishIntent()
        {
            Debug.Log("[WheelIntent] Completed " + active + ".");
            active = WheelIntent.None;
            if (executor != null) executor.PlanFinished -= OnPlanFinishedRefill;
        }
    }
}
