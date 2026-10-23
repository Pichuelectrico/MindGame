using UnityEditor;
using UnityEngine;

namespace MindGame.Editor
{
    // [F46] Wire Boss Laser: creates the BossLaserBeam ActionDefinition, adds it
    // to the ActionManifest and rebuilds the animator controllers (BuildController
    // auto-creates the "BossLaserBeam" state with motion = OneHandSword_Combo —
    // state name == ActionId contract). Idempotent: re-running fixes or skips.
    public static class BossLaserWireMenu
    {
        private const string AssetPath = "Assets/Data/Actions/BossLaserBeam.asset";
        private const string TornadoAssetPath = "Assets/Data/Actions/BossLaserTornado.asset";
        private const string ManifestPath = "Assets/Data/Actions/ActionManifest.asset";

        [MenuItem("MindGame/Setup/Wire Boss Laser")]
        public static void WireBossLaser()
        {
            EnsureLaserAction();
            EnsureTornadoAction(); // [F47]
            EnsureManifestEntry(AssetPath);
            EnsureManifestEntry(TornadoAssetPath);
            SandboxBuilder.BuildAnimatorControllers(); // crea los estados "BossLaserBeam"/"BossLaserTornado" con motion = OneHandSword_Combo
            AssetDatabase.SaveAssets();
            Debug.Log("[BossLaser] DONE. Laser recto + tornado listos (idempotente): assets + manifest + estados en BossController.");
        }

        // (a) ActionDefinition: canalizado BossOnly — [BAL-F1] 4.5s de cast
        //     (1.5s wind-up telegrafiado + 3s de beam), 12m, 30 de stamina,
        //     sin target lock (el componente hace el snap-turn).
        //     impactTimeFraction 0.33 ≈ 1.5s/4.5s: el ActionImpact cae cuando
        //     el beam se enciende (el componente no lo consume, es solo
        //     informativo). travelDistance 0: el boss NO se mueve casteando.
        private static void EnsureLaserAction()
        {
            var def = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionDefinition>(AssetPath);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
                var so = new SerializedObject(def);
                so.FindProperty("actionId").stringValue = "BossLaserBeam";
                so.FindProperty("displayName").stringValue = "Láser del Mutante (canalizado 4.5s)";
                so.FindProperty("clipName").stringValue = "OneHandSword_Combo";
                so.FindProperty("duration").floatValue = 4.5f;
                so.FindProperty("range").floatValue = 12f; // [F49] balance 20 -> 12
                so.FindProperty("travelDistance").floatValue = 0f;
                so.FindProperty("staminaCost").floatValue = 30f;
                so.FindProperty("requiresWeapon").boolValue = false;
                so.FindProperty("needsTarget").boolValue = false;
                so.FindProperty("roleRestriction").intValue = (int)Game.Actions.ActionRole.BossOnly;
                so.FindProperty("fallbackActionId").stringValue = "Idle";
                so.FindProperty("isHeavy").boolValue = false;
                so.FindProperty("impactTimeFraction").floatValue = 0.33f; // [BAL-F1] ≈ 1.5s/4.5s
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(def, AssetPath);
                Debug.Log("[BossLaser] Created BossLaserBeam.asset");
            }
            else if (def.ClipName != "OneHandSword_Combo" || def.RoleRestriction != Game.Actions.ActionRole.BossOnly
                     || !Mathf.Approximately(def.Range, 12f) // [F49] self-heal also forces the balance range
                     || !Mathf.Approximately(def.Duration, 4.5f)) // [BAL-F1] self-heal al rebalance
            {
                // Self-heal si el asset fue revertido a mano o quedó del rebalance viejo.
                var so = new SerializedObject(def);
                so.FindProperty("clipName").stringValue = "OneHandSword_Combo";
                so.FindProperty("roleRestriction").intValue = (int)Game.Actions.ActionRole.BossOnly;
                so.FindProperty("duration").floatValue = 4.5f;
                so.FindProperty("range").floatValue = 12f;
                so.FindProperty("staminaCost").floatValue = 30f;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(def);
                Debug.Log("[BossLaser] BossLaserBeam.asset re-aplicado (self-heal idempotente).");
            }
        }

        // (b) Manifest: agrega la definición al final si falta (mismo patrón
        //     que EnsureBossPowerUpAction / BuildSuperPunch).
        private static void EnsureManifestEntry(string assetPath)
        {
            var def = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionDefinition>(assetPath);
            var manifest = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionManifest>(ManifestPath);
            if (def == null || manifest == null)
            {
                Debug.LogError("[BossLaser] No se encontró el asset o el manifest — revisa rutas.");
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
            Debug.Log("[BossLaser] " + def.ActionId + " agregado al ActionManifest");
        }

        // [F47] Tornado: canalizado BossOnly — [BAL-F1] 5.5s de cast (1.5s
        // wind-up telegrafiado + 4s de giro = 2x360 a 180deg/s), 12m, 35 de
        // stamina, sin target lock. El beam rota, el transform del boss NO
        // (el componente lo maneja).
        private static void EnsureTornadoAction()
        {
            // [F55 addendum] El usuario pidió el tornado con la animación
            // "power up": usamos SwordShield_PowerUp (existe en
            // Assets/Animations/Sword/). BuildController re-asigna
            // state.motion en cada BuildAnimatorControllers, así que el
            // estado "BossLaserTornado" del BossController se cura solo.
            const string tornadoClip = "SwordShield_PowerUp";
            var def = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionDefinition>(TornadoAssetPath);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
                var so = new SerializedObject(def);
                so.FindProperty("actionId").stringValue = "BossLaserTornado";
                so.FindProperty("displayName").stringValue = "Láser tornado del Mutante (2x360°)";
                so.FindProperty("clipName").stringValue = tornadoClip;
                so.FindProperty("duration").floatValue = 5.5f;
                so.FindProperty("range").floatValue = 12f; // [F49] balance 20 -> 12
                so.FindProperty("travelDistance").floatValue = 0f;
                so.FindProperty("staminaCost").floatValue = 35f;
                so.FindProperty("requiresWeapon").boolValue = false;
                so.FindProperty("needsTarget").boolValue = false;
                so.FindProperty("roleRestriction").intValue = (int)Game.Actions.ActionRole.BossOnly;
                so.FindProperty("fallbackActionId").stringValue = "Idle";
                so.FindProperty("isHeavy").boolValue = false;
                so.FindProperty("impactTimeFraction").floatValue = 0.27f; // [BAL-F1] ≈ 1.5s wind-up / 5.5s
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(def, TornadoAssetPath);
                Debug.Log("[BossLaser] Created BossLaserTornado.asset");
            }
            else if (def.ClipName != tornadoClip || def.RoleRestriction != Game.Actions.ActionRole.BossOnly
                     || !Mathf.Approximately(def.Range, 12f) // [F49] self-heal also forces the balance range
                     || !Mathf.Approximately(def.Duration, 5.5f)) // [BAL-F1] self-heal al rebalance
            {
                var so = new SerializedObject(def);
                so.FindProperty("clipName").stringValue = tornadoClip;
                so.FindProperty("roleRestriction").intValue = (int)Game.Actions.ActionRole.BossOnly;
                so.FindProperty("duration").floatValue = 5.5f;
                so.FindProperty("range").floatValue = 12f;
                so.FindProperty("staminaCost").floatValue = 35f;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(def);
                Debug.Log("[BossLaser] BossLaserTornado.asset re-aplicado (self-heal idempotente, clip=" + tornadoClip + ").");
            }
        }
    }
}
