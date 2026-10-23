using Game.Actions;

namespace Game.Execution
{
    // One validated entry: definition + resolved direction + optional target id.
    public sealed class QueuedAction
    {
        public readonly ActionDefinition Definition;
        public readonly ActionDirection Direction;
        public readonly string TargetId;

        public QueuedAction(ActionDefinition definition, ActionDirection direction, string targetId)
        {
            Definition = definition;
            Direction = direction;
            TargetId = targetId;
        }
    }
}
