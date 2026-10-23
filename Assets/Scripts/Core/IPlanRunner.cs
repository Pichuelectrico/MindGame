using System;

namespace Game.Core
{
    // Minimal runner contract so TimeStateMachine never depends on ActionExecutor.
    // The real executor adapts to this; tests use a stub.
    public interface IPlanRunner
    {
        bool IsRunning { get; }
        void StartPlan();
        void StopPlan();
        event Action PlanFinished;
    }
}
