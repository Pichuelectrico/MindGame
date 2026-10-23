using System;
using UnityEngine;

namespace Game.Actions
{
    public enum ActionRole
    {
        Any = 0,
        BossOnly = 1,
        PlayerOnly = 2
    }

    public enum ActionDirection
    {
        None = 0,
        Left = 1,
        Center = 2,
        Right = 3
    }

    [CreateAssetMenu(fileName = "ActionDefinition", menuName = "MindGame/Action Definition")]
    public class ActionDefinition : ScriptableObject
    {
        [SerializeField] private string actionId = "Idle";
        [SerializeField] private string displayName = "Idle";
        [SerializeField] private string clipName = "Idle";
        [SerializeField] private float duration = 1f;
        [SerializeField] private float range = 0f;
        // Measured hips travel of the source clip (meters, signed along forward).
        // The clips were detrended to play in place; the executor moves the
        // transform by travelDistance over the action duration instead.
        [SerializeField] private float travelDistance = 0f;
        [SerializeField] private float staminaCost = 0f;
        [SerializeField] private bool requiresWeapon = false;
        [SerializeField] private bool needsTarget = false;
        [SerializeField] private ActionDirection direction = ActionDirection.None;
        [SerializeField] private ActionRole roleRestriction = ActionRole.Any;
        [SerializeField] private string fallbackActionId = "Idle";
        [SerializeField] private bool useRootMotion = false;
        [SerializeField] private string[] aliases = Array.Empty<string>();
        [Tooltip("Heavy hits knock down (fall + stand up) instead of a plain stagger.")]
        [SerializeField] private bool isHeavy = false;

        // [F7] Impact timing per action: fraction of Duration at which
        // ActionExecutor fires ActionImpact. Default 0.5 = legacy behavior;
        // existing .asset files inherit it untouched. Appended LAST on purpose
        // so the serialized field order of existing assets stays stable.
        [SerializeField, Range(0f, 1f)] private float impactTimeFraction = 0.5f;

        public string ActionId => actionId;
        public string DisplayName => displayName;
        public string ClipName => clipName;
        public float Duration => duration;
        public float Range => range;
        public float TravelDistance => travelDistance;
        public float StaminaCost => staminaCost;
        public bool RequiresWeapon => requiresWeapon;
        public bool NeedsTarget => needsTarget;
        public ActionDirection Direction => direction;
        public ActionRole RoleRestriction => roleRestriction;
        public string FallbackActionId => fallbackActionId;
        public bool UseRootMotion => useRootMotion;
        public bool IsHeavy => isHeavy;
        public float ImpactTimeFraction => impactTimeFraction;
        public string[] Aliases => aliases ?? Array.Empty<string>();

        // Pure precondition check. The executor calls this per entity; weapon-aware
        // selection lives here-and-in-ResolveFallback, never hardcoded per character.
        public bool IsAvailable(bool hasWeapon, ActionRole role, float stamina)
        {
            if (requiresWeapon && !hasWeapon) return false;
            if (roleRestriction != ActionRole.Any && roleRestriction != role) return false;
            if (stamina < staminaCost) return false;
            return true;
        }
    }
}
