using Game.Actions;
using Game.Combat;
using Game.Core;
using Game.Execution;
using Game.Planning;
using Game.UI;
using UnityEngine;

namespace Game.Debugging
{
    // Sandbox wiring: CommandInputUI -> PlanParser -> ActionExecutor -> TimeController.
    // Play-mode flow: type a command, Enter -> pause (Planning) -> accept -> Executing
    // -> executor drains the queue -> Running. Space runs the demo chain.
    [DisallowMultipleComponent]
    public sealed class SandboxTestDriver : MonoBehaviour
    {
        public const string DemoPlanJson =
            "{\"actions\":[{\"type\":\"Idle\"},{\"type\":\"Run\"},{\"type\":\"Punch\"},{\"type\":\"Roll\"}]}";

        [SerializeField] private ActionManifest manifest;
        [SerializeField] private CommandInputUI ui;
        [SerializeField] private TimeController time;
        [SerializeField] private ActionExecutor player;
        [SerializeField] private ActionExecutor boss;
        [SerializeField] private bool playerHasWeapon = true;
        [SerializeField] private float playerStamina = 100f;
        [SerializeField] private float bossStamina = 200f;

        private readonly PlanParser parser = new PlanParser();

        private void Start()
        {
            if (player != null)
            {
                if (manifest != null) player.BindManifest(manifest);
                player.Role = ActionRole.Any;
                player.HasWeapon = playerHasWeapon;
                player.Stamina = playerStamina;
            }
            if (boss != null)
            {
                if (manifest != null) boss.BindManifest(manifest);
                boss.Role = ActionRole.BossOnly;
                boss.HasWeapon = false;
                boss.Stamina = bossStamina;
            }
            SyncTickGate(CombatPhase.Running, time != null ? time.Current : CombatPhase.Running);
            Debug.Log("[SandboxTestDriver] Keys: F9 = demo chain | 1 = damage player 3 | 2 = damage boss 3.");
        }

        private void OnEnable()
        {
            if (ui != null) ui.OnCommandSubmitted += OnCommand;
            if (time != null) time.PhaseChanged += SyncTickGate;
            if (player != null) player.PlanFinished += OnPlayerPlanFinished;
        }

        private void OnDisable()
        {
            if (ui != null) ui.OnCommandSubmitted -= OnCommand;
            if (time != null) time.PhaseChanged -= SyncTickGate;
            if (player != null) player.PlanFinished -= OnPlayerPlanFinished;
        }

        private void Update()
        {
            // New Input System (project uses Input System package, not legacy input).
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            if (kb.f9Key.wasPressedThisFrame && (ui == null || !ui.IsFocused))
                RunPlan(DemoPlanJson);
            if (kb.digit1Key.wasPressedThisFrame && player != null)
            {
                var h = player.GetComponent<Game.Combat.HealthSystem>();
                if (h != null) h.ApplyDamage(3);
            }
            if (kb.digit2Key.wasPressedThisFrame && boss != null)
            {
                var h = boss.GetComponent<Game.Combat.HealthSystem>();
                if (h != null) h.ApplyDamage(3);
            }
        }

        public void RunText(string texto)
        {
            if (manifest == null)
            {
                Debug.LogError("[SandboxTestDriver] No manifest bound.");
                ui?.SetFeedback("sin manifiesto", false);
                return;
            }
            var parsed = parser.Parse(texto ?? string.Empty, manifest);
            if (!parsed.Success)
            {
                Debug.LogWarning("[SandboxTestDriver] Parse rejected: "
                    + string.Join("; ", parsed.Errors.ToArray()));
                ui?.SetFeedback("no entendido", false);
                return;
            }
            foreach (var w in parsed.Warnings)
                Debug.Log("[SandboxTestDriver] Parse warning: " + w);
            RunPlan(parsed.PlanJson);
        }

        public bool RunPlan(string planJson)
        {
            if (player == null || time == null)
            {
                Debug.LogError("[SandboxTestDriver] Player executor or TimeController missing.");
                return false;
            }
            if (time.Current != CombatPhase.Running)
            {
                ui?.SetFeedback("ocupado, espera", false);
                return false;
            }
            if (!time.EnterPlanning()) return false;

            // [F36] Ensure approach before melee in Commands mode: if the first action is a melee
            // attack and the nearest enemy is out of range, prepend a Run action towards that enemy.
            string finalPlan = EnsureApproachBeforeMelee(planJson);

            if (!player.LoadPlan(finalPlan))
            {
                Debug.LogWarning("[SandboxTestDriver] LoadPlan failed.");
                ui?.SetFeedback("plan inválido", false);
                time.CancelPlanning();
                return false;
            }
            ui?.SetFeedback("plan aceptado", true);
            ui?.ClearInput();
            if (!time.AcceptPlan(player))
            {
                time.CancelPlanning();
                return false;
            }
            return true;
        }

        // [F36] Mirrors BotController.EnsureApproachBeforeMelee for the player Commands path.
        // If the plan's first action is a melee attack (Range > 0) and the nearest living
        // enemy (grunt or awake boss) is farther than Range, prepend a Run towards that enemy.
        private string EnsureApproachBeforeMelee(string planJson)
        {
            if (string.IsNullOrEmpty(planJson) || manifest == null || player == null) return planJson;

            // Extract first action type from plan JSON (simple string parse, no allocation-heavy JSON).
            int typeIdx = planJson.IndexOf("\"type\":\"", System.StringComparison.Ordinal);
            if (typeIdx < 0) return planJson;
            int start = typeIdx + 8;
            int end = planJson.IndexOf('"', start);
            if (end < 0) return planJson;
            string firstActionId = planJson.Substring(start, end - start);

            // Find the action definition to check Range.
            var def = manifest.Find(firstActionId);
            if (def == null || def.Range <= 0f) return planJson; // not a melee attack (or no range limit)

            // Find nearest living enemy (grunt or awake boss) — same logic as CombatDirector.NearestLivingEnemy
            var playerHealth = player.GetComponent<HealthSystem>();
            if (playerHealth == null) return planJson;

            HealthSystem nearest = null;
            float bestDist = float.MaxValue;
            foreach (var h in FindObjectsByType<HealthSystem>(FindObjectsSortMode.None))
            {
                if (h == null || h == playerHealth || h.IsDead) continue;
                // Grunts are always valid targets for the player.
                // Boss is valid only when awake (not dormant).
                if (GruntSpawner.IsDormantBoss(h.gameObject)) continue;
                float d = Vector3.Distance(player.transform.position, h.transform.position);
                if (d < bestDist) { bestDist = d; nearest = h; }
            }
            if (nearest == null) return planJson; // no enemies -> attack in place is fine

            if (bestDist <= def.Range) return planJson; // already in range

            // Prepend Run action targeting the nearest enemy by gameObject name (ActionExecutor.MapTarget resolves it).
            string targetId = nearest.gameObject.name;
            string runAction = "{\"type\":\"Run\",\"target\":\"" + targetId + "\"}";
            // Insert after {"actions":[  ->  {"actions":[{"type":"Run","target":"..."},
            int insertIdx = planJson.IndexOf("{\"actions\":[", System.StringComparison.Ordinal);
            if (insertIdx < 0) return planJson;
            insertIdx += "{\"actions\":[".Length;
            return planJson.Insert(insertIdx, runAction + ",");
        }

        private void OnCommand(string text) => RunText(text);

        private void OnPlayerPlanFinished()
        {
            if (time != null) time.OnPlanFinished();
        }

        private void SyncTickGate(CombatPhase from, CombatPhase to)
        {
            bool ticking = time != null ? time.ControllersTicking : true;
            if (player != null) player.TickingEnabled = ticking;
            if (boss != null) boss.TickingEnabled = ticking;
        }
    }
}
