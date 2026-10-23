using UnityEngine;

namespace Game.Combat
{
    // Resolves penetration against the arena's solid walls/pillars for actors
    // that use transform.position movement instead of Rigidbody physics.
    [DisallowMultipleComponent]
    public sealed class CharacterCollisionGate : MonoBehaviour
    {
        [SerializeField] private float radius = 0.45f;
        [SerializeField] private float height = 1.8f;
        [SerializeField] private float skin = 0.02f;
        private CapsuleCollider probe;
        private readonly Collider[] hits = new Collider[32];

        private void Awake()
        {
            probe = gameObject.AddComponent<CapsuleCollider>();
            probe.isTrigger = true;
            probe.radius = radius;
            probe.height = Mathf.Max(height, radius * 2f);
            probe.center = Vector3.up * (probe.height * 0.5f);
            probe.enabled = false;
        }

        private void LateUpdate()
        {
            if (probe == null) return;
            probe.enabled = true;
            int count = Physics.OverlapCapsuleNonAlloc(
                transform.position + probe.center - Vector3.up * (probe.height * 0.5f - radius),
                transform.position + probe.center + Vector3.up * (probe.height * 0.5f - radius),
                radius, hits, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var obstacle = hits[i];
                if (obstacle == null || obstacle == probe || !IsArenaSolid(obstacle)) continue;
                if (Physics.ComputePenetration(probe, transform.position, transform.rotation,
                    obstacle, obstacle.transform.position, obstacle.transform.rotation,
                    out var direction, out var distance))
                    transform.position += direction * (distance + skin);
            }
            probe.enabled = false;
        }

        private static bool IsArenaSolid(Collider collider)
        {
            string n = collider.gameObject.name;
            return n.StartsWith("Arena_Wall") || n.StartsWith("Arena_Pillar");
        }

        // Runtime hardening: guarantees the gate exists on a combatant even if the
        // scene's serialized CharacterCollisionGate was lost (hand-edited scene save).
        // Idempotent — no-op when a gate is already present.
        public static void EnsureOn(GameObject go)
        {
            if (go == null || go.GetComponent<CharacterCollisionGate>() != null) return;
            go.AddComponent<CharacterCollisionGate>();
        }
    }
}
