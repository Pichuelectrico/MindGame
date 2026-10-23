using System.Globalization;
using UnityEngine;

namespace Game.AI
{
    // (de)serialization. [F38] Context diet: ToJson emits a COMPACT payload —
    // self (boss) + player + up to MaxLlmEntities-2 nearest others, positions
    // rounded to 1 decimal, and ONLY the fields a decision actually consumes.
    // Rotation, availableActions, currentActionId and isTargetable never
    // influenced a prompt (the system prompt already lists usable ids; only
    // isAttacking is read), so they are dropped from the wire format. FromJson
    // stays JsonUtility-based: the compact JSON roundtrips the kept fields and
    // absent ones fall back to sane defaults (Normalize).
    public static class CombatStateSerializer
    {
        public const int MaxLlmEntities = 5; // self + player + 3 nearest

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string ToJson(CombatStateSnapshot snapshot, bool pretty = false)
        {
            var s = snapshot ?? new CombatStateSnapshot();
            var src = s.entities ?? System.Array.Empty<EntityState>();

            // Keep: boss (fallback first entity), player avatar, then nearest
            // others to the self by XZ distance until MaxLlmEntities total.
            EntityState self = null, player = null;
            foreach (var e in src)
            {
                if (e == null) continue;
                if (self == null && e.isBoss) self = e;
                if (player == null && e.isPlayerAvatar) player = e;
            }
            if (self == null && src.Length > 0) self = src[0];
            var kept = new System.Collections.Generic.List<EntityState>(MaxLlmEntities);
            if (self != null) kept.Add(self);
            if (player != null && player != self) kept.Add(player);
            if (kept.Count < MaxLlmEntities)
            {
                float sx = Pos(self, 0), sz = Pos(self, 2);
                var others = new System.Collections.Generic.List<EntityState>();
                foreach (var e in src)
                {
                    if (e == null || kept.Contains(e)) continue;
                    others.Add(e);
                }
                others.Sort((a, b) => Dist2(a, sx, sz).CompareTo(Dist2(b, sx, sz)));
                foreach (var e in others)
                {
                    if (kept.Count >= MaxLlmEntities) break;
                    kept.Add(e);
                }
            }

            var sb = new System.Text.StringBuilder(512);
            sb.Append("{\"phase\":\"").Append(Escape(s.phase)).Append('"');
            sb.Append(",\"elapsedTime\":").Append(OneDec(s.elapsedTime));
            sb.Append(",\"entities\":[");
            bool first = true;
            foreach (var e in kept) AppendEntity(sb, e, ref first);
            sb.Append(']');
            AppendDistances(sb, s, kept);
            sb.Append(",\"swordState\":\"").Append(Escape(s.swordState)).Append('"');
            AppendVec(sb, ",\"swordPosition\":", s.swordPosition);
            sb.Append('}');
            return sb.ToString();
        }

        public static CombatStateSnapshot FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return new CombatStateSnapshot();
            var parsed = JsonUtility.FromJson<CombatStateSnapshot>(json);
            if (parsed == null) return new CombatStateSnapshot();
            Normalize(parsed);
            return parsed;
        }

        // JsonUtility leaves absent arrays null; normalize so consumers never null-check.
        private static void Normalize(CombatStateSnapshot s)
        {
            if (s.entities == null) s.entities = System.Array.Empty<EntityState>();
            if (s.distances == null) s.distances = System.Array.Empty<EntityDistance>();
            if (s.swordPosition == null) s.swordPosition = new float[3];
            if (s.swordState == null) s.swordState = "Unknown";
            foreach (var e in s.entities)
            {
                if (e == null) continue;
                if (e.position == null) e.position = new float[3];
                if (e.rotation == null) e.rotation = new float[3];
                if (e.availableActions == null) e.availableActions = System.Array.Empty<string>();
                if (e.currentActionId == null) e.currentActionId = string.Empty;
            }
        }

        private static void AppendEntity(System.Text.StringBuilder sb, EntityState e, ref bool first)
        {
            if (e == null) return;
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":\"").Append(Escape(e.id)).Append('"');
            sb.Append(",\"name\":\"").Append(Escape(e.name)).Append('"');
            sb.Append(",\"isBoss\":").Append(e.isBoss ? "true" : "false");
            sb.Append(",\"isPlayerAvatar\":").Append(e.isPlayerAvatar ? "true" : "false");
            sb.Append(",\"health\":").Append(Num(e.health));
            sb.Append(",\"maxHealth\":").Append(Num(e.maxHealth));
            sb.Append(",\"stamina\":").Append(Num(e.stamina));
            sb.Append(",\"weaponEquipped\":").Append(e.weaponEquipped ? "true" : "false");
            sb.Append(",\"isAttacking\":").Append(e.isAttacking ? "true" : "false");
            if (e.furious) sb.Append(",\"furious\":true"); // [F45] absent = false (compact wire)
            AppendVec(sb, ",\"position\":", e.position);
            sb.Append('}');
        }

        // Distances only between KEPT entities: the model never sees a pair it
        // cannot ground on one of the listed ids.
        private static void AppendDistances(System.Text.StringBuilder sb, CombatStateSnapshot s,
            System.Collections.Generic.List<EntityState> kept)
        {
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (var e in kept)
                if (e != null && !string.IsNullOrEmpty(e.id)) ids.Add(e.id);
            sb.Append(",\"distances\":[");
            bool first = true;
            var pairs = s.distances ?? System.Array.Empty<EntityDistance>();
            foreach (var d in pairs)
            {
                if (d == null || !ids.Contains(d.fromId) || !ids.Contains(d.toId)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"fromId\":\"").Append(Escape(d.fromId))
                    .Append("\",\"toId\":\"").Append(Escape(d.toId))
                    .Append("\",\"distance\":").Append(OneDec(d.distance)).Append('}');
            }
            sb.Append(']');
        }

        private static void AppendVec(System.Text.StringBuilder sb, string prefix, float[] v)
        {
            sb.Append(prefix).Append('[')
                .Append(OneDec(v != null && v.Length > 0 ? v[0] : 0f)).Append(',')
                .Append(OneDec(v != null && v.Length > 1 ? v[1] : 0f)).Append(',')
                .Append(OneDec(v != null && v.Length > 2 ? v[2] : 0f)).Append(']');
        }

        private static float Pos(EntityState e, int axis)
        {
            if (e == null || e.position == null || e.position.Length <= axis) return 0f;
            return e.position[axis];
        }

        private static float Dist2(EntityState e, float sx, float sz)
        {
            float dx = Pos(e, 0) - sx, dz = Pos(e, 2) - sz;
            return dx * dx + dz * dz;
        }

        // Invariant culture is MANDATORY: es-ES floats use ',' and would break JSON.
        private static string OneDec(float v) => v.ToString("0.0", Inv);
        private static string Num(float v) => v.ToString("0.##", Inv);

        private static string Escape(string value) => (value ?? string.Empty)
            .Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
