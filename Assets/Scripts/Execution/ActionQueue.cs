using System.Collections.Generic;

namespace Game.Execution
{
    // FIFO with capacity guard. Pure: no MonoBehaviour, no Animator.
    public sealed class ActionQueue
    {
        public const int DefaultCapacity = 6;

        private readonly Queue<QueuedAction> queue = new Queue<QueuedAction>();
        private readonly int capacity;

        public ActionQueue(int capacity = DefaultCapacity)
        {
            this.capacity = capacity > 0 ? capacity : DefaultCapacity;
        }

        public int Count => queue.Count;
        public bool IsEmpty => queue.Count == 0;
        public int Capacity => capacity;

        public bool TryEnqueue(QueuedAction action)
        {
            if (action == null || action.Definition == null) return false;
            if (queue.Count >= capacity) return false;
            queue.Enqueue(action);
            return true;
        }

        public bool TryDequeue(out QueuedAction action)
        {
            if (queue.Count == 0) { action = null; return false; }
            action = queue.Dequeue();
            return true;
        }

        public bool TryPeek(out QueuedAction action)
        {
            if (queue.Count == 0) { action = null; return false; }
            action = queue.Peek();
            return true;
        }

        public void Clear() => queue.Clear();
    }
}
