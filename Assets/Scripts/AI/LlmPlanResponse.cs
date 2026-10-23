using System;
using UnityEngine;

namespace Game.AI
{
    // One requested LLM action. Same schema as the player plan contract:
    // {"type":"Dodge"} or {"type":"Attack","target":"right"}.
    [Serializable]
    public sealed class LlmRequestedAction
    {
        public string type;
        public string target;
    }

    // LLM plan response DTO: {"actions":[...]}. Max 6 actions per project contract.
    [Serializable]
    public sealed class LlmPlanResponse
    {
        public const int MaxActions = 6;

        public LlmRequestedAction[] actions = Array.Empty<LlmRequestedAction>();

        // Minimal validation: non-null, 1..MaxActions entries, non-empty type each.
        public bool IsValid()
        {
            if (actions == null || actions.Length == 0 || actions.Length > MaxActions)
                return false;
            foreach (var a in actions)
            {
                if (a == null || string.IsNullOrWhiteSpace(a.type))
                    return false;
            }
            return true;
        }

        public static LlmPlanResponse FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return Invalid();
            try
            {
                var parsed = JsonUtility.FromJson<LlmPlanResponse>(json);
                if (parsed == null || parsed.actions == null)
                    return Invalid();
                return parsed;
            }
            catch (Exception)
            {
                return Invalid();
            }
        }

        public static LlmPlanResponse Invalid()
        {
            return new LlmPlanResponse { actions = Array.Empty<LlmRequestedAction>() };
        }
    }
}
