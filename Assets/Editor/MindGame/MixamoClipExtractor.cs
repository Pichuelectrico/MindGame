using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MindGame.Editor
{
    public static class MixamoClipExtractor
    {
        private const string SourceFolder = "Assets/Art/Characters";

        // source FBX file name (without extension) -> "subfolder/clipName"
        private static readonly KeyValuePair<string, string>[] RenameMap =
        {
            KV("Mutant Idle", "Boss/Mutant_Idle"),
            KV("Mutant Walking", "Boss/Mutant_Walking"),
            KV("Mutant Run", "Boss/Mutant_Run"),
            KV("Mutant Punch", "Boss/Mutant_Punch"),
            KV("Mutant Jump Attack", "Boss/Mutant_JumpAttack"),
            KV("Front Twist Flip", "Boss/FrontTwistFlip"),
            KV("Military Signaling", "Boss/MilitarySignaling_PreCombatIdle"),
            KV("Idle", "Shared/Idle"),
            KV("Idle fight", "Shared/IdleFight"),
            KV("Running", "Shared/Run"),
            KV("Crouched Walking", "Shared/CrouchedWalk"),
            KV("Jump", "Shared/Jump"),
            KV("Jump Over", "Shared/JumpOver"),
            KV("Running Slide", "Shared/RunningSlide"),
            KV("Quick Roll To Run", "Shared/QuickRollToRun"),
            KV("Front Flip", "Shared/FrontFlip_EvadeAlt"),
            KV("Dodging", "Shared/Dodge"),
            KV("Stunned", "Shared/Stunned"),
            KV("Stand Up", "Shared/StandUp"),
            KV("Getting Hit", "Shared/GettingHit_HitReact"),
            KV("Punching", "Shared/Punch"),
            KV("Combo Punch", "Shared/ComboPunch"),
            KV("Flying Knee Punch Combo", "Shared/FlyingKneeCombo"),
            KV("Frisbee Throw", "Shared/ThrowWeapon_FrisbeeThrow"),
            KV("Sword And Shield Death", "Shared/SwordShield_Death"),
            KV("Left Block", "Shared/Block_Left"),
            KV("Center Block", "Shared/Block_Center"),
            KV("Sword And Shield Attack", "Sword/SwordShield_Attack"),
            KV("Sword And Shield Slash", "Sword/SwordShield_Slash"),
            KV("Sword And Shield Slash (1)", "Sword/SwordShield_Slash2"),
            KV("Sword And Shield Kick", "Sword/SwordShield_Kick"),
            KV("Sword And Shield Jump", "Sword/SwordShield_Jump"),
            KV("Sword And Shield Power Up", "Sword/SwordShield_PowerUp"),
            KV("One Hand Sword Combo", "Sword/OneHandSword_Combo"),
        };

        private static KeyValuePair<string, string> KV(string k, string v)
        {
            return new KeyValuePair<string, string>(k, v);
        }

        [MenuItem("MindGame/Setup/Extract Mixamo Clips")]
        public static void ExtractMixamoClips()
        {
            int extracted = 0, skipped = 0, errors = 0;
            foreach (var entry in RenameMap)
            {
                string fbxPath = SourceFolder + "/" + entry.Key + ".fbx";
                string destPath = "Assets/Animations/" + entry.Value + ".anim";
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(destPath);
                    if (!AssetDatabase.IsValidFolder(dir))
                    {
                        System.IO.Directory.CreateDirectory(dir);
                        AssetDatabase.Refresh();
                    }
                    if (AssetDatabase.LoadAssetAtPath<AnimationClip>(destPath) != null)
                    {
                        Debug.Log("[MixamoClipExtractor] Exists, skipping: " + destPath);
                        skipped++;
                        continue;
                    }
                    var clips = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
                    AnimationClip src = null;
                    foreach (var o in clips)
                    {
                        var c = o as AnimationClip;
                        if (c != null && !c.name.StartsWith("__preview__"))
                        {
                            src = c;
                            break;
                        }
                    }
                    if (src == null)
                    {
                        Debug.LogError("[MixamoClipExtractor] No clip found in: " + fbxPath);
                        errors++;
                        continue;
                    }
                    var copy = Object.Instantiate(src);
                    AssetDatabase.CreateAsset(copy, destPath);
                    Debug.Log("[MixamoClipExtractor] Extracted: " + fbxPath + " -> " + destPath);
                    extracted++;
                }
                catch (System.Exception ex)
                {
                    Debug.LogError("[MixamoClipExtractor] ERROR " + fbxPath + ": " + ex.Message);
                    errors++;
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(string.Format("[MixamoClipExtractor] DONE. Extracted={0} Skipped={1} Errors={2} (idempotent: re-run is safe)",
                extracted, skipped, errors));
        }

        [MenuItem("MindGame/Setup/Verify Retarget")]
        public static void VerifyRetarget()
        {
            int ok = 0, warn = 0;
            foreach (var entry in RenameMap)
            {
                string fbxPath = SourceFolder + "/" + entry.Key + ".fbx";
                var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                if (importer == null)
                {
                    Debug.LogWarning("[MixamoClipExtractor] Not found: " + fbxPath);
                    warn++;
                    continue;
                }
                if (importer.animationType != ModelImporterAnimationType.Human)
                {
                    Debug.LogWarning("[MixamoClipExtractor] NOT Humanoid: " + fbxPath);
                    warn++;
                    continue;
                }
                ok++;
            }
            Debug.Log(string.Format("[MixamoClipExtractor] VERIFY DONE. Humanoid OK={0} Warnings={1}", ok, warn));
        }
    }
}
