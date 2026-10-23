using UnityEditor;
using UnityEngine;

namespace MindGame.Editor
{
    // [F52] Wire Retreat: creates the Retreat ActionDefinition (the Distance
    // persona's escape — backward walk via NEGATIVE TravelDistance, clamped
    // inside the arena by ActionExecutor), adds it to the ActionManifest and
    // rebuilds the animator controllers (BuildController auto-creates the
    // "Retreat" state with motion = CrouchedWalk — state name == ActionId
    // contract; the clip plays IN PLACE, the executor displaces the transform).
    // Idempotent: re-running fixes or skips.
    public static class RetreatWireMenu
    {
        private const string AssetPath = "Assets/Data/Actions/Retreat.asset";
        private const string ManifestPath = "Assets/Data/Actions/ActionManifest.asset";

        [MenuItem("MindGame/Setup/Wire Retreat Action")]
        public static void WireRetreat()
        {
            EnsureRetreatAction();
            EnsureManifestEntry();
            SandboxBuilder.BuildAnimatorControllers(); // crea el estado "Retreat" con motion = CrouchedWalk
            AssetDatabase.SaveAssets();
            Debug.Log("[Retreat] DONE. Acción de retroceso lista (idempotente): asset + manifest + estado en BossController.");
        }

        // ActionDefinition: BossOnly — 1s de retroceso programático.
        // travelDistance -1.1 (SIGNED along forward => camina hacia atrás a
        // 1.1 m/s; ApplyLocomotion lo aplica tal cual). stamina 10, sin target
        // lock, sin arma, clip CrouchedWalk en el sitio (root motion off via
        // useRootMotion 0 — la locomoción la hace el executor). El clamp de
        // arena vive en ActionExecutor (cross-zone anunciado).
        private static void EnsureRetreatAction()
        {
            var def = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionDefinition>(AssetPath);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
                var so = new SerializedObject(def);
                so.FindProperty("actionId").stringValue = "Retreat";
                so.FindProperty("displayName").stringValue = "Retroceso táctico (Distance)";
                so.FindProperty("clipName").stringValue = "CrouchedWalk";
                so.FindProperty("duration").floatValue = 1f;
                so.FindProperty("range").floatValue = 0f;
                so.FindProperty("travelDistance").floatValue = -1.1f; // negativo = hacia atrás
                so.FindProperty("staminaCost").floatValue = 10f;
                so.FindProperty("requiresWeapon").boolValue = false;
                so.FindProperty("needsTarget").boolValue = false;
                so.FindProperty("roleRestriction").intValue = (int)Game.Actions.ActionRole.BossOnly;
                so.FindProperty("fallbackActionId").stringValue = "IdleFight";
                so.FindProperty("isHeavy").boolValue = false;
                so.FindProperty("impactTimeFraction").floatValue = 0.5f; // sin impacto real
                so.FindProperty("useRootMotion").boolValue = false; // locomoción programática
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(def, AssetPath);
                Debug.Log("[Retreat] Created Retreat.asset");
            }
            else if (def.ClipName != "CrouchedWalk" || def.RoleRestriction != Game.Actions.ActionRole.BossOnly
                     || !Mathf.Approximately(def.TravelDistance, -1.1f))
            {
                // Self-heal si el asset fue revertido a mano.
                var so = new SerializedObject(def);
                so.FindProperty("clipName").stringValue = "CrouchedWalk";
                so.FindProperty("roleRestriction").intValue = (int)Game.Actions.ActionRole.BossOnly;
                so.FindProperty("duration").floatValue = 1f;
                so.FindProperty("travelDistance").floatValue = -1.1f;
                so.FindProperty("staminaCost").floatValue = 10f;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(def);
                Debug.Log("[Retreat] Retreat.asset re-aplicado (self-heal idempotente).");
            }
        }

        // Manifest: agrega la definición al final si falta (patrón F46).
        private static void EnsureManifestEntry()
        {
            var def = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionDefinition>(AssetPath);
            var manifest = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionManifest>(ManifestPath);
            if (def == null || manifest == null)
            {
                Debug.LogError("[Retreat] No se encontró el asset o el manifest — revisa rutas.");
                return;
            }

            var mso = new SerializedObject(manifest);
            var actionsProp = mso.FindProperty("actions");
            for (int i = 0; i < actionsProp.arraySize; i++)
            {
                if (actionsProp.GetArrayElementAtIndex(i).objectReferenceValue == def)
                {
                    mso.Dispose();
                    return; // ya está
                }
            }
            int idx = actionsProp.arraySize;
            actionsProp.InsertArrayElementAtIndex(idx);
            actionsProp.GetArrayElementAtIndex(idx).objectReferenceValue = def;
            mso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(manifest);
            Debug.Log("[Retreat] " + def.ActionId + " agregado al ActionManifest");
        }
    }
}
