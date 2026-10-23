using System;
using System.Collections.Generic;
using Game.Execution;
using UnityEngine;

namespace Game.VFX
{
    public enum AttackTelegraphKind
    {
        None = 0,
        Melee = 1,
        Throw = 2,
    }

    // Visual telegraph: emissive pulse when ActionExecutor starts a melee (red)
    // or throw (yellow) action. Polls Current each frame — does not edit executor.
    [DisallowMultipleComponent]
    public sealed class AttackTelegraph : MonoBehaviour
    {
        private static readonly string[] MeleeTokens =
        {
            "AttackSword",
            "AttackKick",
            "AttackCombo",
            "Punch",
            "BossHeavyAttack",
            "MutantPunch",
            "SuperPunch",
        };

        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        [SerializeField] private ActionExecutor executor;
        [SerializeField] private float flashDuration = 0.35f;
        [SerializeField] private float emissionBoost = 2.5f;
        [SerializeField] private Color meleeColor = new Color(1f, 0.12f, 0.08f, 1f);
        [SerializeField] private Color throwColor = new Color(1f, 0.85f, 0.15f, 1f);

        private readonly List<Material> materials = new List<Material>();
        private readonly List<Color> originalEmission = new List<Color>();
        private readonly List<bool> hadEmissionKeyword = new List<bool>();

        private string lastActionId;
        private bool flashing;
        private float flashElapsed;
        private Color flashPeak;
        private bool materialsCached;

        public ActionExecutor Executor => executor;

        // Pure categorization for EditMode tests and runtime.
        public static AttackTelegraphKind Categorize(string actionId)
        {
            if (string.IsNullOrEmpty(actionId)) return AttackTelegraphKind.None;

            if (actionId.IndexOf("Throw", StringComparison.OrdinalIgnoreCase) >= 0)
                return AttackTelegraphKind.Throw;

            for (int i = 0; i < MeleeTokens.Length; i++)
            {
                var token = MeleeTokens[i];
                if (string.Equals(actionId, token, StringComparison.OrdinalIgnoreCase))
                    return AttackTelegraphKind.Melee;
                if (actionId.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return AttackTelegraphKind.Melee;
            }

            return AttackTelegraphKind.None;
        }

        private void Awake()
        {
            if (executor == null)
                executor = GetComponent<ActionExecutor>();
            CacheMaterials();
        }

        private void OnEnable()
        {
            lastActionId = ReadCurrentActionId();
        }

        private void OnDisable()
        {
            RestoreEmission();
            flashing = false;
        }

        private void OnDestroy()
        {
            RestoreEmission();
            flashing = false;
        }

        private void Update()
        {
            if (flashing)
                TickFlash();

            PollActionChange();
        }

        private void PollActionChange()
        {
            var id = ReadCurrentActionId();
            if (string.Equals(id, lastActionId, StringComparison.Ordinal))
                return;

            lastActionId = id;
            var kind = Categorize(id);
            if (kind == AttackTelegraphKind.None)
                return;

            BeginFlash(kind == AttackTelegraphKind.Melee ? meleeColor : throwColor);
        }

        private string ReadCurrentActionId()
        {
            if (executor == null) return null;
            var current = executor.Current;
            if (current == null || current.Action == null || current.Action.Definition == null)
                return null;
            return current.Action.Definition.ActionId;
        }

        private void BeginFlash(Color baseColor)
        {
            if (!materialsCached)
                CacheMaterials();
            if (materials.Count == 0)
                return;

            // Abort cleanly if this GO (or all renderers) went inactive mid-life.
            if (!isActiveAndEnabled)
                return;

            flashPeak = baseColor * emissionBoost;
            flashElapsed = 0f;
            flashing = true;
            ApplyEmission(flashPeak);
        }

        private void TickFlash()
        {
            if (!isActiveAndEnabled)
            {
                RestoreEmission();
                flashing = false;
                return;
            }

            // Mid-flash SetActive(false) on a child mesh: drop pulse without leaving glow.
            if (!AnyRendererActive())
            {
                RestoreEmission();
                flashing = false;
                return;
            }

            flashElapsed += Time.deltaTime;
            float duration = Mathf.Max(0.01f, flashDuration);
            float t = Mathf.Clamp01(flashElapsed / duration);
            if (t >= 1f)
            {
                RestoreEmission();
                flashing = false;
                return;
            }

            // Peak at t=0, lerp back to original.
            for (int i = 0; i < materials.Count; i++)
            {
                var mat = materials[i];
                if (mat == null) continue;
                if (!mat.HasProperty(EmissionColorId)) continue;
                var color = Color.Lerp(flashPeak, originalEmission[i], t);
                mat.SetColor(EmissionColorId, color);
            }
        }

        private bool AnyRendererActive()
        {
            var renderers = GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].enabled && renderers[i].gameObject.activeInHierarchy)
                    return true;
            }
            return false;
        }

        private void CacheMaterials()
        {
            materials.Clear();
            originalEmission.Clear();
            hadEmissionKeyword.Clear();

            // Include inactive children so dormant boss / pooled grunts still wire up.
            var renderers = GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                var renderer = renderers[r];
                if (renderer == null) continue;
                // .materials instances — never touch sharedMaterials.
                var instances = renderer.materials;
                for (int i = 0; i < instances.Length; i++)
                {
                    var mat = instances[i];
                    if (mat == null) continue;
                    bool had = mat.IsKeywordEnabled("_EMISSION");
                    mat.EnableKeyword("_EMISSION");
                    Color original = mat.HasProperty(EmissionColorId)
                        ? mat.GetColor(EmissionColorId)
                        : Color.black;
                    materials.Add(mat);
                    originalEmission.Add(original);
                    hadEmissionKeyword.Add(had);
                }
            }

            materialsCached = true;
        }

        private void ApplyEmission(Color color)
        {
            for (int i = 0; i < materials.Count; i++)
            {
                var mat = materials[i];
                if (mat == null) continue;
                if (!mat.HasProperty(EmissionColorId)) continue;
                mat.EnableKeyword("_EMISSION");
                mat.SetColor(EmissionColorId, color);
            }
        }

        private void RestoreEmission()
        {
            for (int i = 0; i < materials.Count; i++)
            {
                var mat = materials[i];
                if (mat == null) continue;
                if (mat.HasProperty(EmissionColorId))
                    mat.SetColor(EmissionColorId, originalEmission[i]);
                if (i < hadEmissionKeyword.Count && !hadEmissionKeyword[i])
                    mat.DisableKeyword("_EMISSION");
            }
        }
    }
}
