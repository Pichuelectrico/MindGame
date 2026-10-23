using System;

namespace Game.AI
{
    [Serializable]
    public sealed class EntityState
    {
        public string id;
        public string name;
        public bool isBoss;
        public bool isPlayerAvatar;
        public float[] position = new float[3];
        public float[] rotation = new float[3];
        public float health;
        public float maxHealth;
        public float stamina;
        public bool weaponEquipped;
        public bool isTargetable;
        public string[] availableActions = Array.Empty<string>();
        public string currentActionId = string.Empty; // empty when idle
        public bool isAttacking;
        public bool furious; // [F45] boss enrage flag (wire: emitted only when true)
    }

    [Serializable]
    public sealed class EntityDistance
    {
        public string fromId;
        public string toId;
        public float distance;
    }

    [Serializable]
    public sealed class CombatStateSnapshot
    {
        public string phase;
        public float elapsedTime;
        public EntityState[] entities = Array.Empty<EntityState>();
        public EntityDistance[] distances = Array.Empty<EntityDistance>();
        // Contested sword (additive F6 field; absent in old JSON -> defaults).
        public string swordState = "Unknown";
        public float[] swordPosition = new float[3];
    }
}
