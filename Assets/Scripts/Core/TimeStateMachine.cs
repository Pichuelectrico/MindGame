using System;

namespace Game.Core
{
    // Pure phase machine for active pause. No MonoBehaviour, no statics: fully testable.
    // Tactical pause is LOGICAL: while Planning, autonomous controllers must not Tick
    // (see ControllersTicking). Time.timeScale is never touched here.
    public sealed class TimeStateMachine
    {
        private IPlanRunner runner;

        public CombatPhase Current { get; private set; } = CombatPhase.Running;

        // Extension point for future global slow-motion. Not used for tactical pause.
        public float TimeScale { get; set; } = 1f;

        // Enemy Bots tick at 0 Hz while Planning (frozen); they tick in Running/Executing.
        public bool ControllersTicking => Current != CombatPhase.Planning;

        public event Action<CombatPhase, CombatPhase> PhaseChanged;

        // Running -> Planning. Idempotent: already Planning returns true without event.
        public bool EnterPlanning()
        {
            if (Current == CombatPhase.Planning) return true;
            if (Current != CombatPhase.Running) return false;
            SetPhase(CombatPhase.Planning);
            return true;
        }

        // Planning -> Running (player closed menu without accepting).
        public bool CancelPlanning()
        {
            if (Current == CombatPhase.Running) return true;
            if (Current != CombatPhase.Planning) return false;
            SetPhase(CombatPhase.Running);
            return true;
        }

        // Planning -> Executing. Starts the runner; its PlanFinished event ends the plan.
        public bool AcceptPlan(IPlanRunner planRunner)
        {
            if (planRunner == null) return false;
            if (Current != CombatPhase.Planning) return false;
            ReleaseRunner();
            runner = planRunner;
            runner.PlanFinished += HandleRunnerFinished;
            runner.StartPlan();
            SetPhase(CombatPhase.Executing);
            return true;
        }

        // Executing -> Running (runner finished, or external completion signal).
        // Idempotent: already Running returns true without event.
        public bool OnPlanFinished()
        {
            if (Current == CombatPhase.Running) return true;
            if (Current != CombatPhase.Executing) return false;
            ReleaseRunner();
            SetPhase(CombatPhase.Running);
            return true;
        }

        // Executing -> Running on damage interrupt. Stops the runner first.
        public bool InterruptToRunning()
        {
            if (Current == CombatPhase.Running) return true;
            if (Current != CombatPhase.Executing) return false;
            var r = runner;
            ReleaseRunner();
            if (r != null) r.StopPlan();
            SetPhase(CombatPhase.Running);
            return true;
        }

        private void HandleRunnerFinished() => OnPlanFinished();

        private void ReleaseRunner()
        {
            if (runner != null)
            {
                runner.PlanFinished -= HandleRunnerFinished;
                runner = null;
            }
        }

        private void SetPhase(CombatPhase next)
        {
            var prev = Current;
            Current = next;
            PhaseChanged?.Invoke(prev, next);
        }
    }
}
