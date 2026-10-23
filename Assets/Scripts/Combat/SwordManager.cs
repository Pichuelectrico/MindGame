using System;
using System.Collections.Generic;
using Game.Execution;
using UnityEngine;

namespace Game.Combat
{
    public enum SwordState { InMap, Equipped, InFlight, Respawning }

    // Contested sword: procedural placeholder (handle + guard + blade, metallic)
    // until the user drops a real model in Assets/Art/Props/ and assigns
    // swordPrefabOverride. First fighter in range auto-equips (AttackSword on);
    // ThrowWeapon impact loses it; 30s respawn. Everyone starts disarmed.
    // Late execution order: this Start (mass disarm) must run AFTER character
    // setup Starts (e.g. SandboxTestDriver re-arms the player), otherwise the
    // player keeps HasWeapon=true and TryPickup skips them forever.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public sealed class SwordManager : MonoBehaviour
    {
        [SerializeField] private Vector3 spawnPoint = new Vector3(0f, 0.6f, 5f);
        [SerializeField] private float respawnSeconds = 30f;
        [SerializeField] private float pickupRadius = 1.5f;
        [SerializeField] private float pollInterval = 0.25f;
        [SerializeField] private GameObject swordPrefabOverride;

        public SwordState State { get; private set; } = SwordState.Respawning;
        public Vector3 SwordPosition => swordGO != null ? swordGO.transform.position : spawnPoint;

        public event Action<SwordState> OnSwordStateChanged;

        private GameObject swordGO;
        private ActionExecutor holder;
        private float pollTimer;
        private float respawnTimer;
        private float flightTimer;
        private float skipLogTimer;
        private Vector3 flightFrom;
        private Vector3 flightTo;
        private const float FlightDuration = 0.4f;

        private void Start()
        {
            // Everyone starts unarmed: the sword on the map decides.
            foreach (var exec in FindObjectsOfType<ActionExecutor>())
                exec.HasWeapon = false;
            SpawnInMap();
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        // Public for EditMode tests (Update delegates here).
        public void Tick(float dt)
        {
            switch (State)
            {
                case SwordState.InMap:
                    pollTimer += dt;
                    if (pollTimer >= pollInterval)
                    {
                        pollTimer = 0f;
                        TryPickup();
                    }
                    break;
                case SwordState.InFlight:
                    flightTimer += dt;
                    float t = Mathf.Clamp01(flightTimer / FlightDuration);
                    if (swordGO != null)
                        swordGO.transform.position = Vector3.Lerp(flightFrom, flightTo, t);
                    if (t >= 1f)
                        StartRespawn();
                    break;
                case SwordState.Respawning:
                    respawnTimer += dt;
                    if (respawnTimer >= respawnSeconds)
                        SpawnInMap();
                    break;
            }
        }

        public void ForceSpawn()
        {
            if (holder != null) UnequipInternal();
            SpawnInMap();
        }

        private void SpawnInMap()
        {
            EnsureSwordObject();
            swordGO.transform.SetParent(null);
            swordGO.transform.position = spawnPoint;
            swordGO.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            swordGO.SetActive(true);
            SetState(SwordState.InMap);
        }

        private void TryPickup()
        {
            ActionExecutor best = null;
            float bestDist = float.MaxValue;
            skipLogTimer += Time.deltaTime;
            bool mayLog = skipLogTimer >= 2f;
            if (mayLog) skipLogTimer = 0f;
            foreach (var exec in FindObjectsOfType<ActionExecutor>())
            {
                if (exec == null || !exec.isActiveAndEnabled) continue;
                float d = Vector3.Distance(exec.transform.position, SwordPosition);
                if (d > pickupRadius * 3f) continue; // far: silent
                var hp = exec.GetComponent<HealthSystem>();
                if (exec.HasWeapon)
                {
                    if (mayLog)
                        Debug.Log("[Sword] " + exec.name + " in range (" + d.ToString("F2")
                            + "m) but already armed; skipping.");
                    continue;
                }
                if (hp != null && hp.IsDead)
                {
                    if (mayLog)
                        Debug.Log("[Sword] " + exec.name + " in range but dead; skipping.");
                    continue;
                }
                if (d <= pickupRadius && d < bestDist) { bestDist = d; best = exec; }
            }
            if (best != null)
            {
                try { Equip(best); }
                catch (System.Exception ex)
                {
                    Debug.LogError("[Sword] Equip failed for " + best.name + ": " + ex.Message);
                }
            }
        }

        private void Equip(ActionExecutor exec)
        {
            holder = exec;
            holder.HasWeapon = true;
            holder.ActionImpact += OnHolderImpact;
            var hp = holder.GetComponent<HealthSystem>();
            if (hp != null) hp.Died += OnHolderDied;
            var animator = holder.GetComponent<Animator>();
            // GetBoneTransform THROWS when avatar is null (it never returns null
            // in that case), so guard first: broken avatar still gets the sword,
            // attached to the body root instead of the hand.
            Transform hand = null;
            try
            {
                hand = animator != null && animator.avatar != null && animator.isHuman
                    ? animator.GetBoneTransform(HumanBodyBones.RightHand)
                    : null;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[Sword] humanoid lookup failed (" + ex.Message + ").");
            }
            if (hand == null)
            {
                // Generic rig is this project's canon (no avatar): the Mixamo hand
                // still exists as a named transform — deep-search it by name.
                // Works identically for player, grunts and the boss.
                hand = FindBoneDeep(holder.transform, "RightHand");
            }
            if (hand == null)
            {
                Debug.LogWarning("[Sword] hand bone not found — attached to body root.");
            }
            if (swordGO != null)
            {
                if (hand != null)
                {
                    swordGO.transform.SetParent(hand);
                    swordGO.transform.localPosition = Vector3.zero;
                    swordGO.transform.localRotation = Quaternion.identity;
                }
                else
                {
                    swordGO.transform.SetParent(holder.transform);
                    swordGO.transform.localPosition = new Vector3(0.4f, 1f, 0.3f);
                }
            }
            SetState(SwordState.Equipped);
            Debug.Log("[Sword] " + holder.name + " picked up the sword.");
        }

        private static Transform FindBoneDeep(Transform root, string boneName)
        {
            // Mixamo bones are "mixamorig:RightHand"; suffix match also covers
            // the plain name. Pre-order DFS returns the parent bone before its
            // index/thumb children ("...RightHandIndex1" can't shadow it).
            if (root.name.EndsWith(boneName, System.StringComparison.OrdinalIgnoreCase)) return root;
            foreach (Transform child in root)
            {
                var hit = FindBoneDeep(child, boneName);
                if (hit != null) return hit;
            }
            return null;
        }

        private void OnHolderImpact(ActionInstance instance)
        {
            var def = instance?.Action?.Definition;
            if (def == null || def.ActionId != "ThrowWeapon") return;
            // Thrown: sword flies forward from the hand, then it's lost.
            Vector3 from = swordGO != null ? swordGO.transform.position : holder.transform.position;
            flightFrom = from;
            flightTo = from + holder.transform.forward * 6f;
            flightTo.y = Mathf.Max(0.3f, flightTo.y);
            UnequipInternal();
            flightTimer = 0f;
            SetState(SwordState.InFlight);
            Debug.Log("[Sword] Thrown! It is lost...");
        }

        private void OnHolderDied(HealthSystem hp)
        {
            hp.Died -= OnHolderDied;
            // Lost in the chaos: back to the respawn cycle.
            if (holder != null) UnequipInternal();
            StartRespawn();
        }

        private void UnequipInternal()
        {
            if (holder != null)
            {
                holder.HasWeapon = false;
                holder.ActionImpact -= OnHolderImpact;
                var hp = holder.GetComponent<HealthSystem>();
                if (hp != null) hp.Died -= OnHolderDied;
                holder = null;
            }
            if (swordGO != null) swordGO.transform.SetParent(null);
        }

        private void StartRespawn()
        {
            if (swordGO != null) swordGO.SetActive(false);
            respawnTimer = 0f;
            SetState(SwordState.Respawning);
        }

        private void SetState(SwordState next)
        {
            State = next;
            OnSwordStateChanged?.Invoke(next);
        }

        private void EnsureSwordObject()
        {
            if (swordGO != null) return;
            if (swordPrefabOverride != null)
            {
                swordGO = Instantiate(swordPrefabOverride);
                swordGO.name = "Sword";
                return;
            }
            swordGO = new GameObject("SwordPlaceholder");
            var metal = LitMaterial(new Color(0.75f, 0.78f, 0.85f), 0.9f, 0.6f);
            var gripMat = LitMaterial(new Color(0.25f, 0.15f, 0.1f), 0f, 0.4f);
            AddPart("Handle", PrimitiveType.Cylinder, new Vector3(0.05f, 0.28f, 0.05f), new Vector3(0f, 0.14f, 0f), gripMat);
            AddPart("Guard", PrimitiveType.Cube, new Vector3(0.3f, 0.04f, 0.07f), new Vector3(0f, 0.3f, 0f), gripMat);
            AddPart("Blade", PrimitiveType.Cube, new Vector3(0.09f, 1.1f, 0.02f), new Vector3(0f, 0.87f, 0f), metal);
        }

        private static Material LitMaterial(Color color, float metallic, float smoothness)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null; // keep the default primitive material
            var mat = new Material(shader);
            mat.color = color;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            return mat;
        }

        private void AddPart(string name, PrimitiveType type, Vector3 scale, Vector3 localPos, Material mat)
        {
            var part = GameObject.CreatePrimitive(type);
            part.name = name;
            part.transform.SetParent(swordGO.transform, false);
            part.transform.localScale = scale;
            part.transform.localPosition = localPos;
            var renderer = part.GetComponent<Renderer>();
            if (renderer != null && mat != null) renderer.material = mat;
            var collider = part.GetComponent<Collider>();
            if (collider != null)
            {
                if (Application.isPlaying) Destroy(collider);
                else DestroyImmediate(collider);
            }
        }
    }
}
