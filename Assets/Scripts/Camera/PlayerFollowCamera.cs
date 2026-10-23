using UnityEngine;

namespace Game.Camera
{
    public enum CameraMode { ChaseBack, GlobalOverhead }

    // Third-person camera with two modes: ChaseBack (follows behind player facing,
    // with optional mouse look in Manual mode) and GlobalOverhead (tactical top-down).
    // Active during logical pause as well.
    [DisallowMultipleComponent]
    public sealed class PlayerFollowCamera : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private CameraMode mode = CameraMode.ChaseBack;
        [SerializeField] private float chaseDistance = 5.5f;
        [SerializeField] private float defaultPitch = 45f;
        [SerializeField] private float shoulderOffset = 0.6f; // over-the-shoulder: lateral shift on the camera's right
        [SerializeField] private float lookAtHeight = 2.2f;
        [SerializeField] private float positionDamping = 5f;
        [SerializeField] private float rotationDamping = 5f;
        [SerializeField] private float minGroundY = 1f;
        [SerializeField] private Vector3 overheadCenter = Vector3.zero;
        [SerializeField] private float overheadHeight = 25f;
        [SerializeField] private Game.GameMode.GameModeManager modeManager;

        [Header("Mouse Look (Manual Mode)")]
        [Tooltip("0 = auto-facing only (Commands mode); 1 = mouse delta rotates yaw/pitch (Manual mode).")]
        [SerializeField] private float mouseInfluence = 1f;
        [SerializeField] private float mouseSensitivity = 0.25f;

        private float smoothedYaw;
        private float accumulatedYaw;
        private float accumulatedPitch;

        public CameraMode Mode => mode;
        public float MouseInfluence => mouseInfluence;

        public void Bind(Transform followTarget) => target = followTarget;
        public void BindModeManager(Game.GameMode.GameModeManager m) => modeManager = m;

        public void SetMode(CameraMode next) => mode = next;

        public void SetMouseInfluence(float value)
        {
            float prev = mouseInfluence;
            mouseInfluence = Mathf.Clamp01(value);
            if (mouseInfluence <= 0f)
            {
                accumulatedYaw = 0f;
                accumulatedPitch = 0f;
            }
            else if (prev <= 0f && mouseInfluence > 0f)
            {
                // [F25] When entering mouse-look (Manual mode), anchor smoothedYaw to current camera forward
                // so mouse delta accumulation starts seamlessly from the current view with zero snap, drift or enemy lock-on.
                smoothedYaw = YawOf(transform.forward);
                accumulatedYaw = 0f;
                accumulatedPitch = 0f;
            }
        }

        private void Start()
        {
            smoothedYaw = YawOf(transform.forward);
            if (modeManager == null)
                modeManager = FindFirstObjectByType<Game.GameMode.GameModeManager>();
        }

        private void Update()
        {
            if (modeManager == null)
                modeManager = FindFirstObjectByType<Game.GameMode.GameModeManager>();

            if (modeManager != null)
            {
                bool isManual = modeManager.CurrentMode == Game.GameMode.GameMode.Manual;
                if (isManual && mouseInfluence <= 0f) SetMouseInfluence(1f);
                else if (!isManual && mouseInfluence > 0f) SetMouseInfluence(0f);
            }

            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                // [Camera fix] The old digit1/digit2 hotkeys sat right above Q (Dodge)
                // and W: dodging with Q kept flipping the camera to GlobalOverhead,
                // which ignores the mouse by design — reported as "camera doesn't
                // follow the mouse". Replaced with a single safe toggle on C,
                // far from every action key, with a console trace so accidental
                // switches are visible.
                if (kb.cKey.wasPressedThisFrame)
                {
                    var next = mode == CameraMode.GlobalOverhead ? CameraMode.ChaseBack : CameraMode.GlobalOverhead;
                    if (next != mode)
                    {
                        mode = next;
                        Debug.Log("[PlayerFollowCamera] Camera mode -> " + mode +
                            (mode == CameraMode.GlobalOverhead ? " (press C to return to chase cam)" : ""));
                    }
                }
            }

            if (mouseInfluence > 0f)
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse != null)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    accumulatedYaw += delta.x * mouseSensitivity * mouseInfluence;
                    accumulatedPitch -= delta.y * mouseSensitivity * mouseInfluence;
                    accumulatedPitch = Mathf.Clamp(accumulatedPitch, -30f - defaultPitch, 70f - defaultPitch);
                }
            }

            // [F12 / F17 fix] Pistol-style cursor capture: lock + hide while aiming in
            // Manual chase mode; release when the action wheel is open (free
            // cursor needed to click cards) and in overhead/Commands modes.
            bool wantLock = mode == CameraMode.ChaseBack
                && mouseInfluence > 0f && !Game.UI.ActionWheel.WheelOpen;
            var wantedLockState = wantLock ? CursorLockMode.Locked : CursorLockMode.None;
            if (Cursor.lockState != wantedLockState)
                Cursor.lockState = wantedLockState;
            if (Cursor.visible == wantLock)
                Cursor.visible = !wantLock;

            // Re-assert lock if user clicks into window to focus
            // ([F18 compile fix] renamed local: 'mouse' already exists in the
            // mouseInfluence block above — CS0136.)
            var mouseReassert = UnityEngine.InputSystem.Mouse.current;
            if (wantLock && mouseReassert != null && (mouseReassert.leftButton.wasPressedThisFrame || mouseReassert.rightButton.wasPressedThisFrame))
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (mode == CameraMode.GlobalOverhead)
            {
                Vector3 goal = overheadCenter + Vector3.up * overheadHeight;
                float t = 1f - Mathf.Exp(-positionDamping * dt);
                transform.position = Vector3.Lerp(transform.position, goal, t);
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.Euler(90f, 0f, 0f),
                    1f - Mathf.Exp(-rotationDamping * dt));
                return;
            }
            if (target == null) return;

            // [F25] Zero auto-alignment in Manual mode. Auto-facing target.forward ONLY in Commands mode.
            bool inManualMode = (modeManager != null && modeManager.CurrentMode == Game.GameMode.GameMode.Manual) || mouseInfluence > 0f;
            if (!inManualMode)
            {
                Vector3 flat = target.forward;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.0001f)
                {
                    float want = YawOf(flat.normalized);
                    smoothedYaw = Mathf.LerpAngle(smoothedYaw, want, 1f - Mathf.Exp(-rotationDamping * dt));
                }
            }

            // In Manual mode, mouse X/Y rotates camera with clamped pitch (-30..70).
            // In Commands mode, pitch uses defaultPitch (clamped 30..75) and yaw uses smoothedYaw.
            float yaw = smoothedYaw + accumulatedYaw;
            float pitch = inManualMode
                ? Mathf.Clamp(defaultPitch + accumulatedPitch, -30f, 70f)
                : Mathf.Clamp(defaultPitch, 30f, 75f);

            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 desired = target.position + new Vector3(0f, lookAtHeight, 0f) - rot * Vector3.forward * chaseDistance;
            desired += Quaternion.Euler(0f, yaw, 0f) * Vector3.right * shoulderOffset;
            desired.y = Mathf.Max(desired.y, minGroundY);
            transform.position = Vector3.Lerp(transform.position, desired, 1f - Mathf.Exp(-positionDamping * dt));
            if (inManualMode)
            {
                // [F12 / F25 fix] Mouse-look: rotation applied DIRECTLY from the
                // accumulated yaw/pitch (crisp shooter feel). Zero lock-on.
                transform.rotation = rot;
            }
            else
            {
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(target.position + new Vector3(0f, lookAtHeight, 0f) - transform.position),
                    1f - Mathf.Exp(-rotationDamping * dt));
            }
        }

        private static float YawOf(Vector3 dir)
        {
            return Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        }
    }
}
