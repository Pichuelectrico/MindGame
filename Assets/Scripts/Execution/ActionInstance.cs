namespace Game.Execution
{
    // Live FSM state of the currently running action. Pure data; the executor
    // advances Elapsed in Tick and exits at Duration.
    public sealed class ActionInstance
    {
        public readonly QueuedAction Action;
        public float Elapsed;
        public bool ImpactFired;

        public ActionInstance(QueuedAction action)
        {
            Action = action;
            Elapsed = 0f;
        }

        public float Duration => Action != null && Action.Definition != null
            ? Action.Definition.Duration
            : 0f;

        public bool IsComplete => Elapsed >= Duration;
    }
}
