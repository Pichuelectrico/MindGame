using Game.Camera;
using Game.Combat;
using Game.Core;
using Game.Execution;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.GameMode
{
    // [F12] Manual player controller:
    // - WASD movement relative to PlayerFollowCamera (camera-oriented)
    // - R: Roll (dodge burst ~2.5m in 0.5s + 0.4s invulnerability + 1s cooldown)
    // - J: SuperPunch (heavy punch, 25 stamina)
    // - L: ThrowWeapon (requires sword equipped)
    // - H: Sword Slash (requires sword equipped, AttackSword)
    // - Right Click: Melee strike (AttackSword if armed, else Punch)
    // - Space: Jump
    // - Mouse-look active in Manual mode (sets mouseInfluence=1 on PlayerFollowCamera)
    [DisallowMultipleComponent]
    public sealed class PlayerManualController : MonoBehaviour
    {
        [SerializeField] private GameModeManager modeManager;
        [SerializeField] private TimeController time;
        [SerializeField] private ActionExecutor executor;
        [SerializeField] private HealthSystem health;
        [SerializeField] private UnityEngine.Camera targetCamera;
        [SerializeField] private PlayerFollowCamera followCamera;

        [Header("Locomotion")]
        [SerializeField] private float runSpeed = 4f;
        [SerializeField] private float rotationSpeed = 12f;

        [Header("Action IDs")]
        [SerializeField] private string attackActionId = "Punch";
        [SerializeField] private string superPunchActionId = "SuperPunch";
        [SerializeField] private string throwWeaponActionId = "ThrowWeapon";
        [SerializeField] private string swordAttackActionId = "AttackSword";
        [SerializeField] private string blockActionId = "BlockCenter";
        [SerializeField] private string dodgeActionId = "BlockCenter";
        [SerializeField] private string rollActionId = "Roll";
        [SerializeField] private string jumpActionId = "Jump";

        [Header("Attack Tuning")]
        [SerializeField] private float attackCooldownSeconds = 0.8f;

        [Header("Roll Tuning")]
        [SerializeField] private float rollDistance = 3.0f;
        [SerializeField] private float rollDuration = 0.65f;
        [SerializeField] private float rollInvulnerabilityDuration = 0.5f;
        [SerializeField] private float rollCooldown = 0.8f;

        private Animator animator;
        private bool wasMoving;
        private bool isRolling;
        private float rollTimer;
        private Vector3 rollDirection;
        private float nextRollTime = -100f;
        private float nextAttackTime = -100f;

        public float AttackCooldownSeconds
        {
            get => attackCooldownSeconds;
            set => attackCooldownSeconds = value;
        }

        public float NextAttackTime => nextAttackTime;
        public bool IsAttackOnCooldown => Time.time < nextAttackTime;

        public string BlockActionId
        {
            get => blockActionId;
            set => blockActionId = value;
        }

        public string DodgeActionId
        {
            get => dodgeActionId;
            set => dodgeActionId = value;
        }

        public string SwordAttackActionId
        {
            get => swordAttackActionId;
            set => swordAttackActionId = value;
        }

        public void ResetAttackCooldown()
        {
            nextAttackTime = -100f;
        }

        public void TriggerAttackCooldown()
        {
            nextAttackTime = Time.time + attackCooldownSeconds;
        }

        public bool TryExecuteSwordSlash()
        {
            if (executor == null) executor = GetComponent<ActionExecutor>();
            if (Time.time < nextAttackTime) return false;
            if (executor == null || executor.IsRunning) return false;
            if (!executor.HasWeapon) return false;

            if (executor.Stamina >= 12f)
            {
                nextAttackTime = Time.time + attackCooldownSeconds;
                ExecuteAction(swordAttackActionId);
                return true;
            }
            return false;
        }

        // EditMode-friendly wiring
        public void Bind(ActionExecutor exec)
        {
            executor = exec;
        }

        private void Awake()
        {
            if (executor == null) executor = GetComponent<ActionExecutor>();
            if (health == null) health = GetComponent<HealthSystem>();
            if (time == null) time = GetComponent<TimeController>() ?? FindFirstObjectByType<TimeController>();
            if (modeManager == null) modeManager = GetComponent<GameModeManager>() ?? FindFirstObjectByType<GameModeManager>();
            if (followCamera == null) followCamera = FindFirstObjectByType<PlayerFollowCamera>();
            animator = GetComponent<Animator>();
        }

        private void Start()
        {
            UpdateCameraInfluence();
        }

        private void OnEnable()
        {
            if (time != null) time.PhaseChanged += OnPhaseChanged;
            if (modeManager != null) modeManager.OnModeChanged += OnModeChanged;
            UpdateCameraInfluence();
        }

        private void OnDisable()
        {
            if (time != null) time.PhaseChanged -= OnPhaseChanged;
            if (modeManager != null) modeManager.OnModeChanged -= OnModeChanged;
            if (followCamera != null) followCamera.SetMouseInfluence(0f);
        }

        private void OnPhaseChanged(CombatPhase from, CombatPhase to)
        {
            if (to != CombatPhase.Running)
            {
                wasMoving = false;
                isRolling = false;
            }
        }

        private void OnModeChanged(GameMode mode)
        {
            if (mode != GameMode.Manual)
            {
                wasMoving = false;
                isRolling = false;
            }
            UpdateCameraInfluence();
        }

        private void UpdateCameraInfluence()
        {
            if (followCamera == null)
                followCamera = FindFirstObjectByType<PlayerFollowCamera>();

            bool isManual = modeManager == null || modeManager.CurrentMode == GameMode.Manual;
            if (followCamera != null)
                followCamera.SetMouseInfluence(isManual ? 1f : 0f);
        }

        private void Update()
        {
            if (modeManager != null && modeManager.CurrentMode != GameMode.Manual) return;
            if (time != null && time.Current != CombatPhase.Running) return;
            if (health != null && health.IsDead) return;

            // Handle active roll burst
            if (isRolling)
            {
                rollTimer -= Time.deltaTime;
                float rollSpeed = rollDistance / Mathf.Max(0.01f, rollDuration);
                transform.position += rollDirection * (rollSpeed * Time.deltaTime);

                if (rollTimer <= 0f)
                {
                    isRolling = false;
                    // [F17 fix] When roll burst ends, smoothly exit roll pose based on current movement input
                    var currentKb = Keyboard.current;
                    bool moveRequested = currentKb != null && (
                        currentKb.wKey.isPressed || currentKb.upArrowKey.isPressed ||
                        currentKb.sKey.isPressed || currentKb.downArrowKey.isPressed ||
                        currentKb.dKey.isPressed || currentKb.rightArrowKey.isPressed ||
                        currentKb.aKey.isPressed || currentKb.leftArrowKey.isPressed);

                    if (animator != null)
                    {
                        if (moveRequested)
                        {
                            animator.CrossFadeInFixedTime("Run", 0.1f);
                            wasMoving = true;
                        }
                        else
                        {
                            animator.CrossFadeInFixedTime("Idle", 0.15f);
                            wasMoving = false;
                        }
                    }
                }
                return; // Actions/WASD disabled during roll burst
            }

            var kb = Keyboard.current;
            var mouse = Mouse.current;

            // 1. R = ROLL (Dodge burst with i-frames & cooldown)
            if (kb != null && kb.rKey.wasPressedThisFrame)
            {
                if (Time.time >= nextRollTime)
                {
                    StartRoll();
                    return;
                }
            }

            // 2. Q = BLOCK / DEFENSIVE GUARD (BlockCenter action from manifest, 5 stamina)
            if (kb != null && kb.qKey.wasPressedThisFrame)
            {
                if (executor != null && !executor.IsRunning)
                {
                    string chosenBlock = !string.IsNullOrEmpty(blockActionId) ? blockActionId : "BlockCenter";
                    if (string.Equals(chosenBlock, "Roll", System.StringComparison.OrdinalIgnoreCase))
                        chosenBlock = "BlockCenter";

                    float requiredStamina = 5f;
                    if (executor.Stamina >= requiredStamina)
                    {
                        ExecuteAction(chosenBlock);
                    }
                    else
                    {
                        Debug.LogWarning("[PlayerManualController] Not enough stamina for Block (requires " + requiredStamina + ", has " + executor.Stamina + ")");
                    }
                    return;
                }
            }

            // 3. L = THROW WEAPON (Guard hasWeapon + attack cooldown)
            if (kb != null && kb.lKey.wasPressedThisFrame)
            {
                if (Time.time < nextAttackTime) return; // Silent ignore while on cooldown

                if (executor != null && !executor.IsRunning)
                {
                    if (executor.HasWeapon)
                    {
                        nextAttackTime = Time.time + attackCooldownSeconds;
                        ExecuteAction(throwWeaponActionId);
                    }
                    else
                    {
                        Debug.LogWarning("[PlayerManualController] No weapon equipped to throw!");
                    }
                    return;
                }
            }

            // 4. J = SUPER PUNCH (Guard stamina 25 + attack cooldown)
            if (kb != null && kb.jKey.wasPressedThisFrame)
            {
                if (Time.time < nextAttackTime) return; // Silent ignore while on cooldown

                if (executor != null && !executor.IsRunning)
                {
                    if (executor.Stamina >= 25f)
                    {
                        nextAttackTime = Time.time + attackCooldownSeconds;
                        ExecuteAction(superPunchActionId);
                    }
                    else
                    {
                        Debug.LogWarning("[PlayerManualController] Not enough stamina for SuperPunch (requires 25, has " + executor.Stamina + ")");
                    }
                    return;
                }
            }

            // 5. H = SWORD SLASH (requires weapon in hand + attack cooldown)
            if (kb != null && kb.hKey.wasPressedThisFrame)
            {
                if (Time.time < nextAttackTime) return; // Silent ignore while on cooldown

                if (executor != null && !executor.IsRunning)
                {
                    if (executor.HasWeapon)
                    {
                        if (!TryExecuteSwordSlash())
                        {
                            Debug.LogWarning("[PlayerManualController] Not enough stamina for AttackSword (requires 12, has " + executor.Stamina + ")");
                        }
                    }
                    // Silent ignore when not armed
                    return;
                }
            }

            // 6. RIGHT CLICK = MELEE STRIKE (Affordable weapon attack or punch + attack cooldown)
            // [F12 fix] Right-click melee. NO legacy UnityEngine.Input fallback:
            // the project runs Input System only, so any legacy read throws
            // InvalidOperationException EVERY FRAME (killed the whole Update:
            // WASD/R/L/J all dead). Mouse.current is the only read path.
            bool rightClick = mouse != null && mouse.rightButton.wasPressedThisFrame;
            if (rightClick)
            {
                if (Time.time < nextAttackTime) return; // Silent ignore while on cooldown

                if (executor != null && !executor.IsRunning)
                {
                    string chosenMelee = attackActionId; // Default Punch
                    if (executor.HasWeapon && executor.Stamina >= 12f)
                        chosenMelee = swordAttackActionId; // AttackSword
                    else
                        chosenMelee = attackActionId; // Punch

                    nextAttackTime = Time.time + attackCooldownSeconds;
                    ExecuteAction(chosenMelee);
                    return;
                }
            }

            // 5. Space = JUMP / K = Fallback Dodge
            if (executor != null && !executor.IsRunning && kb != null)
            {
                if (kb.spaceKey.wasPressedThisFrame)
                {
                    ExecuteAction(jumpActionId);
                    return;
                }
                if (kb.kKey.wasPressedThisFrame)
                {
                    ExecuteAction(dodgeActionId);
                    return;
                }
            }

            // 6. WASD Movement relative to camera
            float horizontal = 0f;
            float vertical = 0f;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) vertical += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) vertical -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) horizontal += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) horizontal -= 1f;
            }

            bool isMoving = (horizontal != 0f || vertical != 0f);

            if (isMoving && (executor == null || !executor.IsRunning))
            {
                Transform camTransform = targetCamera != null ? targetCamera.transform : (UnityEngine.Camera.main != null ? UnityEngine.Camera.main.transform : null);
                if (camTransform == null)
                {
                    if (followCamera == null) followCamera = FindFirstObjectByType<PlayerFollowCamera>();
                    if (followCamera != null) camTransform = followCamera.transform;
                }

                Vector3 forward = camTransform != null ? camTransform.forward : Vector3.forward;
                Vector3 right = camTransform != null ? camTransform.right : Vector3.right;
                forward.y = 0f;
                right.y = 0f;

                if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
                if (right.sqrMagnitude < 0.01f) right = Vector3.right;
                forward.Normalize();
                right.Normalize();

                Vector3 moveDir = (forward * vertical + right * horizontal);
                if (moveDir.sqrMagnitude > 1f) moveDir.Normalize();

                if (moveDir.sqrMagnitude > 0.001f)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(moveDir);
                    transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
                    transform.position += moveDir * (runSpeed * Time.deltaTime);

                    if (!wasMoving && animator != null)
                    {
                        animator.CrossFadeInFixedTime("Run", 0.1f);
                        wasMoving = true;
                    }
                }
            }
            else
            {
                if (wasMoving && (executor == null || !executor.IsRunning))
                {
                    if (animator != null) animator.CrossFadeInFixedTime("Idle", 0.15f);
                    wasMoving = false;
                }
            }
        }

        private void StartRoll()
        {
            nextRollTime = Time.time + rollCooldown;

            // Determine roll direction: current move input if moving, else current facing
            Transform camTransform = targetCamera != null ? targetCamera.transform : (UnityEngine.Camera.main != null ? UnityEngine.Camera.main.transform : null);
            if (camTransform == null && followCamera != null) camTransform = followCamera.transform;

            float horizontal = 0f;
            float vertical = 0f;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) vertical += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) vertical -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) horizontal += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) horizontal -= 1f;
            }

            Vector3 dir = Vector3.zero;
            if (horizontal != 0f || vertical != 0f)
            {
                Vector3 forward = camTransform != null ? camTransform.forward : Vector3.forward;
                Vector3 right = camTransform != null ? camTransform.right : Vector3.right;
                forward.y = 0f;
                right.y = 0f;
                forward.Normalize();
                right.Normalize();
                dir = (forward * vertical + right * horizontal).normalized;
            }

            if (dir.sqrMagnitude < 0.001f)
                dir = transform.forward;

            rollDirection = dir;
            transform.rotation = Quaternion.LookRotation(rollDirection);

            // Trigger invulnerability on HealthSystem
            if (health == null) health = GetComponent<HealthSystem>();
            if (health != null) health.TriggerInvulnerability(rollInvulnerabilityDuration);

            // Interrupt any ongoing action
            if (executor != null && executor.IsRunning)
                executor.StopPlan();

            isRolling = true;
            rollTimer = rollDuration;
            wasMoving = false;

            if (animator != null)
                animator.CrossFadeInFixedTime(rollActionId, 0.05f);
        }

        private void ExecuteAction(string actionId)
        {
            if (executor == null) executor = GetComponent<ActionExecutor>();
            if (executor == null || string.IsNullOrEmpty(actionId)) return;
            wasMoving = false;
            string planJson = "{\"actions\":[{\"type\":\"" + actionId + "\"}]}";
            if (executor.LoadPlan(planJson))
            {
                executor.StartPlan();
            }
        }
    }
}
