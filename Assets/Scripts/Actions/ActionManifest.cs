using System.Collections.Generic;
using UnityEngine;

namespace Game.Actions
{
    [CreateAssetMenu(fileName = "ActionManifest", menuName = "MindGame/Action Manifest")]
    public class ActionManifest : ScriptableObject
    {
        [SerializeField] private List<ActionDefinition> actions = new List<ActionDefinition>();
        [SerializeField] private int maxPlanSize = 6;

        public IReadOnlyList<ActionDefinition> Actions => actions;
        public int MaxPlanSize => maxPlanSize;

        public ActionDefinition Find(string actionId)
        {
            if (string.IsNullOrEmpty(actionId)) return null;
            foreach (var a in actions)
            {
                if (a == null) continue;
                if (string.Equals(a.ActionId, actionId, System.StringComparison.OrdinalIgnoreCase))
                    return a;
            }
            return null;
        }

        public ActionDefinition FindByAlias(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var direct = Find(key.Trim());
            if (direct != null) return direct;
            foreach (var a in actions)
            {
                if (a == null || a.Aliases == null) continue;
                foreach (var alias in a.Aliases)
                {
                    if (string.Equals(alias, key.Trim(), System.StringComparison.OrdinalIgnoreCase))
                        return a;
                }
            }
            return null;
        }

        public ActionDefinition ResolveFallback(ActionDefinition failed, bool hasWeapon, ActionRole role, float stamina)
        {
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var current = failed;
            while (current != null && seen.Add(current.ActionId))
            {
                var next = Find(current.FallbackActionId);
                if (next == null || next == current) return Find("Idle");
                if (next.IsAvailable(hasWeapon, role, stamina)) return next;
                current = next;
            }
            return Find("Idle");
        }
    }
}
