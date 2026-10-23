using System.Collections.Generic;
using Game.Execution;
using UnityEngine;

namespace Game.Combat
{
    // Proximity heal pickup: player OR boss can take it (grunts excluded: economy).
    // Deactivates on take. Static registry for intent navigation.
    // Restore clamps at MaxHealth (HealthSystem.cs) — cap 100% guaranteed.
    [DisallowMultipleComponent]
    public sealed class HealthPickup : MonoBehaviour
    {
        [SerializeField] private int healAmount = 5;
        [SerializeField] private float pickupRadius = 1.5f;
        // [F27] Orbs reappear after this delay instead of disappearing forever.
        [SerializeField] private float respawnSeconds = 30f;
        private float collectedAt = -1f;
        private Transform visual;

        public static readonly List<HealthPickup> Active = new List<HealthPickup>();

        // Registry-aware check + scene fallback: OnEnable never runs for
        // AddComponent in EditMode (tests), so the registry can lag reality.
        public static bool AnyActive()
        {
            if (Active.Count > 0) return true;
            return FindFirstObjectByType<HealthPickup>() != null;
        }

        public static HealthPickup NearestTo(Vector3 pos)
        {
            HealthPickup best = null;
            float bestDist = float.MaxValue;
            foreach (var p in Active)
            {
                if (p == null || !p.gameObject.activeInHierarchy) continue;
                float d = Vector3.Distance(p.transform.position, pos);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            if (best != null) return best;
            // Fallback for EditMode rigs (registry empty): scan the scene.
            foreach (var p in FindObjectsByType<HealthPickup>(FindObjectsSortMode.None))
            {
                if (p == null || !p.gameObject.activeInHierarchy) continue;
                float d = Vector3.Distance(p.transform.position, pos);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        private void Awake()
        {
            if (transform.childCount == 0)
                BuildPlaceholder();
            visual = transform.childCount > 0 ? transform.GetChild(0) : null;
        }

        private void OnEnable()
        {
            if (!Active.Contains(this)) Active.Add(this);
        }

        private void OnDisable()
        {
            Active.Remove(this);
        }

        private void Update()
        {
            if (collectedAt >= 0f)
            {
                // [F27] Respawn countdown — the GO stays active so this keeps ticking.
                if (Time.time - collectedAt >= respawnSeconds)
                {
                    collectedAt = -1f;
                    SetCollected(false);
                    Debug.Log("[HealthPickup] Orb respawned (" + respawnSeconds + "s).");
                }
                return;
            }
            foreach (var exec in FindObjectsOfType<ActionExecutor>())
            {
                if (exec == null) continue;
                // Allow player OR boss; grunts excluded (economy).
                bool isPlayer = GruntSpawner.IsPlayer(exec.gameObject);
                bool isBoss = GruntSpawner.IsBoss(exec.gameObject);
                if (!isPlayer && !isBoss) continue;
                var hp = exec.GetComponent<HealthSystem>();
                if (hp == null || hp.IsDead) continue;
                if (hp.CurrentHealth >= hp.MaxHealth) continue;
                if (Vector3.Distance(exec.transform.position, transform.position) > pickupRadius) continue;
                hp.Restore(healAmount);
                string who = isPlayer ? "player" : "boss";
                Debug.Log("[HealthPickup] " + who + " (" + exec.name + ") restored +" + healAmount +
                    " HP. Respawn in " + respawnSeconds + "s.");
                SetCollected(true);
                return;
            }
        }

        // [F27] Collected state: visual hidden, registry updated, GO kept active
        // (SetActive(false) would kill the respawn Update tick).
        private void SetCollected(bool collected)
        {
            if (collected)
            {
                collectedAt = Time.time;
                Active.Remove(this);
            }
            else
            {
                if (!Active.Contains(this)) Active.Add(this);
            }
            if (visual != null) visual.gameObject.SetActive(!collected);
        }

        private void BuildPlaceholder()
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = new Color(0.2f, 0.9f, 0.3f);
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.transform.SetParent(transform, false);
            ball.transform.localScale = Vector3.one * 0.4f;
            var r = ball.GetComponent<Renderer>();
            if (r != null) r.material = mat;
            var c = ball.GetComponent<Collider>();
            if (c != null) Destroy(c);
        }
    }
}
