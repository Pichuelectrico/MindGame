using System;
using UnityEngine;

namespace Game.Core
{
    // Thin MonoBehaviour wrapper: every phase decision delegates to TimeStateMachine.
    // No phase logic lives here, so the behaviour stays trivially testable via the machine.
    [DisallowMultipleComponent]
    public sealed class TimeController : MonoBehaviour
    {
        private readonly TimeStateMachine machine = new TimeStateMachine();

        public CombatPhase Current => machine.Current;
        public bool ControllersTicking => machine.ControllersTicking;

        // [F11] Read access to the wrapped pure machine. Consumers that need
        // the machine INSTANCE (ActionWheel -> WheelIntentRunner.RunFromWheel)
        // must reuse THIS one — handing them a new instance would fork the
        // phase state (the wheel would exit a phantom machine while the real
        // controller stays in Planning = frozen game).
        public TimeStateMachine Machine => machine;

        public float TimeScale
        {
            get => machine.TimeScale;
            set => machine.TimeScale = value;
        }

        public event Action<CombatPhase, CombatPhase> PhaseChanged
        {
            add => machine.PhaseChanged += value;
            remove => machine.PhaseChanged -= value;
        }

        public bool EnterPlanning() => machine.EnterPlanning();
        public bool CancelPlanning() => machine.CancelPlanning();
        public bool AcceptPlan(IPlanRunner runner) => machine.AcceptPlan(runner);
        public bool OnPlanFinished() => machine.OnPlanFinished();
        public bool InterruptToRunning() => machine.InterruptToRunning();
    }
}
