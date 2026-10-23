using Game.Execution;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Combat
{
    // [F46] Boss ranged: sustained straight laser beam. Self-wired to the
    // ActionExecutor on the same GO — when an action with id "BossLaserBeam"
    // starts, the boss snap-turns to the player, shows the 1.5s TELEGRAPHED
    // wind-up ([FX-F2] ground marker + charge pillar + purple aura), then
    // fires a 12m beam for exactly 3s ([BAL-F1]) dealing damage in 0.5s ticks
    // to anything standing in the line (horizontal distance to the beam axis
    // <= hitHalfWidth — core 0.10 + margen 0.10). Ticks use
    // HealthSystem.ApplyDamage directly —
    // never heavy (no knockdown), fully dodgeable, the accumulated streak is
    // accepted by design (CombatDirector.OnHealthChanged already reacts).
    //
    // Lifecycle is driven by Tick(dt) (Update passes Time.deltaTime; EditMode
    // tests pass exact deltas) so the cast is testable without Time.time and
    // without play mode. No external assets: the LineRenderers are built in code.
    //
    // [F55] VFX REWRITE — the single red/cyan LineRenderer was INVISIBLE in
    // the user's playtest (no glow, no damage visuals; the AI DID cast — the
    // jsonl shows 5 casts). Diagnosis: the old EnsureVfx assigned a freshly
    // built URP Particles/Unlit material and relied on LineRenderer VERTEX
    // colors (startColor/endColor) for the red->cyan look; with that shader's
    // default color mode the vertex-color path is unreliable and the fallback
    // decision was silent (no log), so a null Shader.Find left the renderer
    // with no usable material. The new VFX is an explicit PURPLE LIGHTNING
    // bolt: TWO child LineRenderers (thin HDR core + wide translucent additive
    // glow) over a 16-segment jittered polyline re-randomized every 0.1s
    // (crackle), with an EXPLICIT shader chain resolved at runtime and logged
    // ONCE ("[BossLaser] VFX shader=X") so a silent failure can never happen
    // again. Damage logic (SegmentHit/ticks/cooldowns) is untouched.
    public class BossLaserBeam : MonoBehaviour
    {
        public enum CastPhase { Idle, WindUp, Beam }

        [Header("Cast")]
        [SerializeField] private string actionId = "BossLaserBeam";
        // [BAL-F1] Rebalance: wind-up TELEGRAFIADO de 1.5s (coordinado con
        // FX-F2) + beam de 3s = evento total 4.5s ("que dure menos" — antes
        // 1+5=6s recto y 7s tornado).
        [SerializeField] private float windUp = 1.5f;
        [SerializeField] private float beamDuration = 3f;
        [SerializeField] private float tickInterval = 0.5f;
        // [BAL-F1] 7/tick: un roce (1-2 ticks) cuesta 7-14 HP — dentro de la
        // banda "sobrevivible ~5-15" del usuario — y el contacto TOTAL ya no
        // es letal a full HP (recto 6x7=42, tornado 8x7=56; antes 10x6=60 y
        // el tornado barría hasta 8 ticks también).
        [SerializeField] private int damagePerTick = 7;

        [Header("Tornado (F47)")]
        [SerializeField] private string tornadoActionId = "BossLaserTornado";
        // [BAL-F1] 4s a 180deg/s = 720deg: MISMAS 2 vueltas completas en
        // menos tiempo (antes 6s a 120deg/s).
        [SerializeField] private float tornadoBeamDuration = 4f;
        [SerializeField] private float spinDegreesPerSecond = 180f;

        [Header("Beam shape")]
        [SerializeField] private float range = 12f; // [F49] balance: 20 -> 12 (medium distance tool)
        // [BAL-F1] HIT = core (0.10) + margen 0.10. ANTES: width 0.8 (half
        // 0.4) — un roce a 0.4m pegaba SIN tocar el rayo visible (el glow
        // mide 0.35 total = half 0.175). El daño ahora respeta lo que se ve.
        [SerializeField] private float hitHalfWidth = 0.2f;
        [SerializeField] private Vector3 anchorLocal = new Vector3(0f, 1.5f, 0.4f); // chest-high, slightly forward

        [Header("VFX (F55 lightning morado)")]
        // [F55] Nombres NUEVOS (los viejos beamStartColor/beamEndColor rojos se
        // retiraron: los valores serializados en escena quedan huérfanos y los
        // defaults morados toman efecto sin editar el YAML de la escena).
        [SerializeField] private Color windUpColor = new Color(0.55f, 0.15f, 1f, 0.4f); // telegraph morada tenue
        [SerializeField] private Color coreColor = new Color(1.8f, 0.6f, 3.0f);         // HDR casi blanco-morado
        [SerializeField] private Color glowColor = new Color(1.2f, 0.2f, 2.0f, 0.35f);  // morado saturado translúcido
        [SerializeField] private int lightningSegments = 16;   // polilínea 14-20 segmentos
        [SerializeField] private float jitterMeters = 0.25f;   // amplitud perpendicular del zigzag
        [SerializeField] private float crackleInterval = 0.1f; // re-randomize cada ~0.1s (crackle)
        [SerializeField] private float coreWidth = 0.10f;
        [SerializeField] private float glowWidth = 0.35f;

        // [F55] Cadena de shaders verificada EN RUNTIME (se loguea UNA vez):
        // URP Particles/Unlit -> Sprites/Default (siempre existe y honra
        // vertex colors) -> Legacy additive. Inyectable para tests (un test
        // fuerza null en el primero para verificar el fallback).
        private static readonly string[] ShaderChain =
        {
            "Universal Render Pipeline/Particles/Unlit",
            "Sprites/Default",
            "Legacy Shaders/Particles/Additive"
        };

        public static Func<string, Shader> ShaderResolver = name => Shader.Find(name);
        private static string loggedShader;

        private ActionExecutor executor;
        private HealthSystem selfHealth;
        private LineRenderer core; // [F55] núcleo fino HDR
        private LineRenderer glow; // [F55] halo ancho translúcido
        private Material coreMat, glowMat;
        // [FX-F2] Telegraphía: pilar de luz en el boss + marcador en el suelo
        // (línea recta / círculo del barrido) + aura emissive por PropertyBlock.
        private LineRenderer pillar;
        private LineRenderer telegraph;
        private Material pillarMat, telegraphMat;
        private readonly List<Renderer> auraRenderers = new List<Renderer>();
        private const float ChargePillarHeight = 2.4f;
        private const float ChargeGroundY = 0.05f;
        private static MaterialPropertyBlock EmptyBlock
        {
            get
            {
                // Lazy: los inicializadores estáticos con UnityEngine.Object
                // lanzan "CreateImpl is not allowed..." al cargar la clase en el
                // editor (se vio en cada carga de escena desde F55).
                if (emptyBlock == null) emptyBlock = new MaterialPropertyBlock();
                return emptyBlock;
            }
        }
        private static MaterialPropertyBlock emptyBlock;
        private float[] jitterX, jitterY; // per-point scalars (0 en los extremos)
        private float crackleAccumulator;
        private uint crackleSeed = 1u; // determinista — tests lo fijan
        private bool windupBright;     // parpadeo del telegraph

        private CastPhase phase = CastPhase.Idle;
        private float phaseLeft;
        private float tickAccumulator;
        private Vector3 beamOrigin;
        private Vector3 beamDir;
        private Vector3 castForward; // forward at cast time — tornado spin axis base
        private bool tornadoMode;    // [F47] beam spins, boss transform stays put
        private float spinAngle;     // degrees advanced during the current beam
        private int tickCount;

        public CastPhase Phase => phase;
        public bool IsBeaming => phase == CastPhase.Beam;
        public int TickCount => tickCount; // test observation
        public float SpinAngle => spinAngle;   // [F47] test observation (deg since beam start)
        public bool TornadoMode => tornadoMode; // [F47] test observation

        // [F55] VFX test observations.
        public LineRenderer CoreRenderer => core;
        public LineRenderer GlowRenderer => glow;
        public int LightningSegments => lightningSegments;
        public uint CrackleSeed { get => crackleSeed; set => crackleSeed = value; }
        public Color CoreColor => coreColor;
        public Color GlowColor => glowColor;
        public Color WindUpColor => windUpColor;

        // [FX-F2] Telegraph observations (tests).
        public LineRenderer TelegraphRenderer => telegraph;
        public LineRenderer ChargePillarRenderer => pillar;

        // [BAL-F1] Balance observations (tests lock the intent).
        public float WindUpSeconds => windUp;
        public float BeamSeconds => beamDuration;
        public float TornadoBeamSeconds => tornadoBeamDuration;
        public int DamagePerTick => damagePerTick;
        public float HitHalfWidth => hitHalfWidth;

        private void Awake()
        {
            selfHealth = GetComponent<HealthSystem>();
            executor = GetComponent<ActionExecutor>();
            if (executor != null)
            {
                executor.ActionStarted += OnActionStarted;
                executor.ActionFinished += OnActionFinished;
            }
            EnsureVfx();
        }

        private void OnDestroy()
        {
            if (executor != null)
            {
                executor.ActionStarted -= OnActionStarted;
                executor.ActionFinished -= OnActionFinished;
            }
        }

        private void OnDisable() => ResetCast();

        private void OnActionStarted(ActionInstance instance)
        {
            if (instance?.Action == null || instance.Action.Definition == null) return;
            var id = instance.Action.Definition.ActionId;
            if (id == actionId) BeginCast(false);
            else if (id == tornadoActionId) BeginCast(true); // [F47]
        }

        private void OnActionFinished(ActionInstance instance, ActionExitReason reason)
        {
            if (instance?.Action == null || instance.Action.Definition == null) return;
            var id = instance.Action.Definition.ActionId;
            if (id != actionId && id != tornadoActionId) return;
            ResetCast(); // cancelled early (knockdown etc.) -> no half-alive beam
        }

        private void Update() => Tick(Time.deltaTime);

        // Testable cast start: snap-turn to the player (when present), freeze
        // the beam axis, enter WindUp. Public so EditMode tests can drive the
        // cast without firing executor events.
        public void StartCast() => BeginCast(false);

        // [F47] Tornado variant: same wind-up, but during the beam the BEAM
        // rotates around the boss's Y axis at spinDegreesPerSecond — the boss
        // transform never turns (no fight with face-target/locomotion).
        public void StartTornadoCast() => BeginCast(true);

        private void BeginCast(bool tornado)
        {
            tornadoMode = tornado;
            spinAngle = 0f;
            var player = GruntSpawner.PlayerRoot;
            if (player != null)
            {
                var toPlayer = player.position - transform.position;
                toPlayer.y = 0f;
                if (toPlayer.sqrMagnitude > 0.0001f)
                    transform.rotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
            }
            castForward = transform.forward;
            beamOrigin = transform.TransformPoint(anchorLocal);
            beamDir = castForward;
            phase = CastPhase.WindUp;
            phaseLeft = windUp;
            tickAccumulator = 0f;
            tickCount = 0;
            crackleAccumulator = 0f;
            ShowBolt(windUpColor, windUpColor, coreWidth * 0.6f, glowWidth * 0.5f);
            // [FX-F2] Telegraphía ANTES del primer tick de daño: marcador en
            // el suelo + pilar de carga + aura emissive del boss.
            ShowTelegraph();
            ApplyChargeAura(true);
        }

        // Deterministic state-machine step. Wind-up (1.5s, [BAL-F1]/[FX-F2]:
        // telegrafiado con marcador + pilar + aura) -> beam (EXACTLY
        // beamDuration seconds straight / tornadoBeamDuration tornado; the
        // wind-up overshoot is dropped and the transitioning dt belongs to the
        // wind-up — the beam starts consuming time NEXT Tick) -> idle. Damage
        // ticks every tickInterval while the beam is alive. Eps guards the
        // transitions: (1 - 0.9) - 0.1 leaves ~2e-8 of float residue that must
        // still count as "phase over".
        private const float Eps = 1e-4f;

        public void Tick(float dt)
        {
            if (phase == CastPhase.Idle || dt <= 0f) return;
            phaseLeft -= dt;

            if (phase == CastPhase.WindUp)
            {
                // [F55] Telegraph morado PARPADEANTE (pulso cada crackle).
                crackleAccumulator += dt;
                if (crackleAccumulator >= crackleInterval)
                {
                    crackleAccumulator %= crackleInterval;
                    windupBright = !windupBright;
                    if (glow != null)
                    {
                        var c = windUpColor;
                        c.a = windupBright ? 0.55f : 0.25f;
                        glow.startColor = c;
                        glow.endColor = c;
                        ApplyHdrColor(glowMat, c);
                        glow.startWidth = glow.endWidth = glowWidth * (windupBright ? 0.65f : 0.35f);
                    }
                    // [FX-F2] El pilar y el marcador parpadean con el crackle.
                    if (pillar != null)
                    {
                        var c = windUpColor;
                        c.a = windupBright ? 0.5f : 0.2f;
                        pillar.startColor = c;
                        pillar.endColor = c;
                        ApplyHdrColor(pillarMat, c);
                        pillar.startWidth = pillar.endWidth = 0.25f * (windupBright ? 1f : 0.6f);
                    }
                    if (telegraph != null)
                    {
                        var c = windUpColor;
                        c.a = windupBright ? 0.45f : 0.2f;
                        telegraph.startColor = c;
                        telegraph.endColor = c;
                        ApplyHdrColor(telegraphMat, c);
                    }
                }
                if (phaseLeft > Eps)
                {
                    // [FX-F2] El aura CRECE durante el wind-up (0.4 -> 1.0):
                    // el player lee "cargando -> a punto de disparar".
                    float progress = 1f - Mathf.Max(phaseLeft, 0f) / Mathf.Max(windUp, Eps);
                    RefreshAura(0.4f + 0.6f * progress);
                    return;
                }
                phase = CastPhase.Beam;
                phaseLeft = tornadoMode ? tornadoBeamDuration : beamDuration;
                tickAccumulator = 0f;
                spinAngle = 0f;
                crackleAccumulator = 0f;
                HideTelegraph();      // [FX-F2] el aviso muere cuando empieza el daño
                ApplyChargeAura(false); // restaura los PropertyBlocks
                ShowBolt(coreColor, glowColor, coreWidth, glowWidth);
                return; // this dt already belonged to the wind-up — no beam step
            }

            // phase == Beam here.
            if (tornadoMode)
            {
                // [F47] The BEAM spins around the boss's Y axis (the boss
                // transform stays put). Absolute angle from castForward —
                // no incremental drift, [BAL-F1] 180deg/s x 4s = exactly 720deg.
                spinAngle += spinDegreesPerSecond * dt;
                beamDir = Quaternion.AngleAxis(spinAngle, Vector3.up) * castForward;
                // [F55] El endpoint sigue rotando CADA FRAME, pero el zigzag
                // SOLO se re-randomiza en el crackle (performance): las mismas
                // amplitudes jitter se re-proyectan sobre el nuevo beamDir.
                ApplyPoints();
            }

            crackleAccumulator += dt;
            if (crackleAccumulator >= crackleInterval)
            {
                crackleAccumulator %= crackleInterval;
                crackleSeed++;
                RerollJitter();
                ApplyPoints();
            }

            tickAccumulator += dt;
            while (tickAccumulator >= tickInterval)
            {
                tickAccumulator -= tickInterval;
                DoDamageTick();
            }
            if (phaseLeft <= Eps) ResetCast();
        }

        private void DoDamageTick()
        {
            tickCount++;
            if (selfHealth == null) selfHealth = GetComponent<HealthSystem>();
            var healths = FindObjectsByType<HealthSystem>(FindObjectsSortMode.None);
            for (int i = 0; i < healths.Length; i++)
            {
                var hs = healths[i];
                if (hs == selfHealth || hs.IsDead) continue;
                // The beam is horizontal: evaluate each entity on the beam's
                // plane (same Y as the anchor) so height offsets never make a
                // standing target miss.
                var p = hs.transform.position;
                p.y = beamOrigin.y;
                if (SegmentHit(beamOrigin, beamDir, range, hitHalfWidth * 2f, p)) // [BAL-F1] hit = core + margen
                    hs.ApplyDamage(damagePerTick);
            }
        }

        // Pure segment<->point intersection: closest point on the segment
        // (origin -> origin + dir * maxDist) to `point`, hit when the
        // perpendicular distance is <= width / 2 (+1cm squared tolerance so the
        // exact edge counts as a hit even when float noise / JIT contraction
        // nudges it an ulp outward). `dir` is normalized internally (need not
        // be unit). Pure math — EditMode tested.
        public static bool SegmentHit(Vector3 origin, Vector3 dir, float maxDist, float width, Vector3 point)
        {
            if (maxDist <= 0f || width <= 0f) return false;
            var d = dir.sqrMagnitude > 0.000001f ? dir.normalized : Vector3.forward;
            var to = point - origin;
            var t = Mathf.Clamp(Vector3.Dot(to, d), 0f, maxDist);
            var closest = origin + d * t;
            var perp = point - closest;
            var half = width * 0.5f;
            return perp.sqrMagnitude <= half * half + 0.0001f;
        }

        // ================================================================
        // [FX-F2] TELEGRAPHÍA — aviso morado ANTES del primer tick de daño.
        // (1) Marcador en el suelo: línea (recto) / círculo del barrido
        //     (tornado), plano al piso para leerse como zona.
        // (2) Pilar de carga morado sobre el boss (la "aurora" — visible
        //     siempre porque usa la misma cadena de shaders que el rayo).
        // (3) Aura emissive púrpura por PropertyBlock en los renderers del
        //     boss (NO toca los assets de material; best-effort: si el shader
        //     no tiene emission activada el block se ignora — el pilar y el
        //     marcador llevan la telegrafía). Canary "_BossLaserCharge"
        //     permite a los tests verificar set/restore.
        // ================================================================

        private void ShowTelegraph()
        {
            EnsureTelegraph();
            if (pillar == null || telegraph == null) return;
            EnsureTelegraphMaterials();

            var ground = transform.position;
            ground.y += ChargeGroundY;

            if (tornadoMode)
            {
                // Círculo del barrido completo (radio = range) — honesto:
                // TODO el disco se lleva ticks durante el giro.
                const int pts = 49; // 48 segmentos, cerrado
                telegraph.positionCount = pts;
                for (int i = 0; i < pts; i++)
                {
                    float a = (float)i / (pts - 1) * 360f * Mathf.Deg2Rad;
                    telegraph.SetPosition(i, ground + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * range);
                }
            }
            else
            {
                // Línea del rayo a venir (proyección al piso).
                const int pts = 12;
                telegraph.positionCount = pts;
                var fwd = castForward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
                fwd.Normalize();
                for (int i = 0; i < pts; i++)
                    telegraph.SetPosition(i, ground + fwd * (range * i / (pts - 1f)));
            }

            var c = windUpColor;
            telegraph.startColor = c;
            telegraph.endColor = c;
            ApplyHdrColor(telegraphMat, c);
            telegraph.startWidth = telegraph.endWidth = 0.12f;

            pillar.SetPosition(0, transform.position + Vector3.up * ChargeGroundY);
            pillar.SetPosition(1, transform.position + Vector3.up * ChargePillarHeight);
            pillar.startColor = c;
            pillar.endColor = c;
            ApplyHdrColor(pillarMat, c);
            pillar.startWidth = pillar.endWidth = 0.25f;

            telegraph.enabled = true;
            pillar.enabled = true;
        }

        private void HideTelegraph()
        {
            if (pillar != null) pillar.enabled = false;
            if (telegraph != null) telegraph.enabled = false;
        }

        private void EnsureTelegraph()
        {
            if (pillar != null && telegraph != null) return;
            pillar = EnsureChildRenderer("LaserChargePillar");
            telegraph = EnsureChildRenderer("LaserTelegraph");
            pillar.positionCount = 2;
            pillar.useWorldSpace = true;
            pillar.alignment = LineAlignment.View; // pilar vertical billboard, como el rayo
            telegraph.useWorldSpace = true;
            telegraph.alignment = LineAlignment.TransformZ; // ribbon PLANO al piso
            // Hijo girado para que +Z mire hacia arriba: el ribbon queda en el suelo.
            telegraph.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            pillar.enabled = false;
            telegraph.enabled = false;
        }

        private void EnsureTelegraphMaterials()
        {
            if (pillarMat == null && pillar != null) { pillarMat = BuildChainMaterial(); if (pillarMat != null) pillar.sharedMaterial = pillarMat; }
            if (telegraphMat == null && telegraph != null) { telegraphMat = BuildChainMaterial(); if (telegraphMat != null) telegraph.sharedMaterial = telegraphMat; }
        }

        private void ApplyChargeAura(bool on)
        {
            if (!on)
            {
                // Restaurar: block vacío -> los materiales vuelven a sus valores.
                for (int i = 0; i < auraRenderers.Count; i++)
                    if (auraRenderers[i] != null)
                        auraRenderers[i].SetPropertyBlock(EmptyBlock);
                auraRenderers.Clear();
                return;
            }
            auraRenderers.Clear();
            var all = GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null || r == core || r == glow || r == pillar || r == telegraph) continue;
                auraRenderers.Add(r);
            }
            RefreshAura(1f);
        }

        private void RefreshAura(float pulse)
        {
            var emission = new Color(0.55f, 0.15f, 1f) * Mathf.Clamp01(pulse);
            for (int i = 0; i < auraRenderers.Count; i++)
            {
                var r = auraRenderers[i];
                if (r == null) continue;
                var block = new MaterialPropertyBlock();
                block.SetColor("_EmissionColor", emission);
                block.SetFloat("_BossLaserCharge", pulse); // canary de test
                r.SetPropertyBlock(block);
            }
        }

        // ================================================================
        // [F55] LIGHTNING VFX — núcleo + halo morados, polilínea zigzag.
        // ================================================================

        // Muestra el rayo en su fase actual. Los colores del beam son HDR
        // (core >1 en canal morado) — el halo lleva alpha translúcido.
        private void ShowBolt(Color coreStart, Color glowStart, float coreW, float glowW)
        {
            EnsureVfx();
            if (core == null || glow == null) return;
            EnsureMaterials();
            // El HDR (>1) no sobrevive en vertex colors (Color32): la intensidad
            // real va en el color del material; el vertex color queda como tinte.
            ApplyHdrColor(coreMat, coreStart);
            ApplyHdrColor(glowMat, glowStart);
            core.startColor = coreStart;
            core.endColor = coreStart;
            core.startWidth = coreW;
            core.endWidth = coreW;
            glow.startColor = glowStart;
            glow.endColor = glowStart;
            glow.startWidth = glowW;
            glow.endWidth = glowW;
            core.enabled = true;
            glow.enabled = true;
            RerollJitter();
            ApplyPoints();
            // [F55] Garantías del cast (assert-log): si algo falla, el log dice
            // QUÉ — nunca más "no se ve y no sé por qué".
            var expected = lightningSegments + 1;
            if (core.positionCount != expected || glow.positionCount != expected)
                Debug.LogError($"[BossLaser] VFX roto: positionCount={core.positionCount}/{glow.positionCount}, esperado {expected}.");
            if (core.sharedMaterial == null || glow.sharedMaterial == null)
                // Warning (no error): el beam sigue visible con el material
                // default de Unity; el test de cadena rota no debe fallar por
                // el log (UTF marca fallos en errores no esperados).
                Debug.LogWarning("[BossLaser] VFX: material null en core/glow — usando default del renderer.");
            if (!core.enabled || !glow.enabled)
                Debug.LogError("[BossLaser] VFX roto: renderer disabled al iniciar el cast.");
        }

        // Re-roll DETERMINISTA de las amplitudes de jitter (crackle): mismo
        // seed -> mismo rayo (tests fijan CrackleSeed).
        public void RerollJitter()
        {
            EnsureCapacity();
            for (int i = 1; i < lightningSegments; i++)
            {
                float t = (float)i / lightningSegments;
                float taper = Mathf.Sin(Mathf.PI * t); // 0 en ambos extremos
                jitterX[i] = (Hash01(crackleSeed, (uint)i, 1u) * 2f - 1f) * jitterMeters * taper;
                jitterY[i] = (Hash01(crackleSeed, (uint)i, 2u) * 2f - 1f) * jitterMeters * taper * 0.5f;
            }
            jitterX[0] = jitterY[0] = 0f;
            jitterX[lightningSegments] = jitterY[lightningSegments] = 0f;
        }

        // Re-proyecta la polilínea sobre (beamOrigin, beamDir) con las
        // amplitudes actuales. Barato (N+1 puntos) — ok cada frame en tornado.
        public void ApplyPoints()
        {
            if (core == null || glow == null) return;
            var side = Vector3.Cross(beamDir, Vector3.up);
            if (side.sqrMagnitude < 1e-6f) side = Vector3.right;
            side.Normalize();
            var upBasis = Vector3.Cross(side, beamDir).normalized;
            int count = lightningSegments + 1;
            for (int i = 0; i < count; i++)
            {
                float t = (float)i / lightningSegments;
                var p = beamOrigin + beamDir * (range * t)
                        + side * jitterX[i] + upBasis * jitterY[i];
                core.SetPosition(i, p);
                glow.SetPosition(i, p);
            }
        }

        // uint hash determinista (xorshift mix) -> [0,1].
        private static float Hash01(uint seed, uint a, uint b)
        {
            uint x = seed ^ (a * 747796405u) ^ (b * 2891336453u);
            x = ((x >> ((int)(x >> 28) + 4)) ^ x) * 277803737u;
            x = (x >> 22) ^ x;
            return (x & 0xFFFFFFu) / (float)0xFFFFFFu;
        }

        private void EnsureCapacity()
        {
            int count = lightningSegments + 1;
            if (jitterX == null || jitterX.Length != count)
            {
                jitterX = new float[count];
                jitterY = new float[count];
            }
        }

        private void EnsureVfx()
        {
            if (core != null && glow != null) return;
            core = EnsureChildRenderer("LaserCore");
            glow = EnsureChildRenderer("LaserGlow");
            int count = lightningSegments + 1;
            core.positionCount = count;
            glow.positionCount = count;
            core.useWorldSpace = true;
            glow.useWorldSpace = true;
            core.alignment = LineAlignment.View; // billboard al camera
            glow.alignment = LineAlignment.View;
            core.enabled = false;
            glow.enabled = false;
            EnsureCapacity();
        }

        private LineRenderer EnsureChildRenderer(string childName)
        {
            var t = transform.Find(childName);
            if (t == null)
            {
                var go = new GameObject(childName);
                go.transform.SetParent(transform, false);
                t = go.transform;
            }
            var lr = t.GetComponent<LineRenderer>();
            if (lr == null) lr = t.gameObject.AddComponent<LineRenderer>();
            return lr;
        }

        // [F57] Wire idempotente: agrega el componente al boss SOLO si falta.
        // El executor dispara ActionStarted y sin un componente escuchando no
        // hay beam ni ticks de daño (bug F55: solo el menú preview lo montaba).
        // Devuelve true si lo agregó (los callers loguean con su prefijo).
        // Helper runtime: testeable en EditMode sin depender de ensamblados
        // de editor (SandboxBuilder está en Assembly-CSharp-Editor).
        public static bool EnsureOn(GameObject host)
        {
            if (host == null) return false;
            if (host.GetComponent<BossLaserBeam>() != null) return false;
            host.AddComponent<BossLaserBeam>();
            return true;
        }

        private void EnsureMaterials()
        {
            // sharedMaterial (NO .material): llamar .material en edit mode
            // instancia una copia y Unity la loguea como ERROR ("leak into
            // the scene") — además es una copia innecesaria en runtime.
            if (coreMat == null) { coreMat = BuildChainMaterial(); if (coreMat != null) core.sharedMaterial = coreMat; }
            if (glowMat == null) { glowMat = BuildChainMaterial(); if (glowMat != null) glow.sharedMaterial = glowMat; }
            // Si la cadena entera falló NO asignamos null: el renderer conserva
            // su material por defecto (Default-Line) — nunca un slot null.
        }

        // Escribe el color por la propiedad que exista en el shader elegido.
        // Es donde vive el HDR real (los vertex colors del LineRenderer se
        // truncan a [0,1] por Color32).
        private static void ApplyHdrColor(Material m, Color c)
        {
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            else if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c);
        }

        private Material BuildChainMaterial()
        {
            foreach (var shaderName in ShaderChain)
            {
                Shader sh = null;
                try { sh = ShaderResolver(shaderName); }
                catch (Exception) { sh = null; }
                if (sh == null) continue;
                var mat = new Material(sh) { hideFlags = HideFlags.DontSave };
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                else if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
                else if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", Color.white);
                if (loggedShader != shaderName)
                {
                    loggedShader = shaderName;
                    Debug.Log("[BossLaser] VFX shader=" + shaderName);
                }
                return mat;
            }
            // Todos null: warn ROJO + material default del renderer (que al
            // menos se dibuje — el default LineRenderer honra vertex colors).
            if (loggedShader != "<default>")
            {
                loggedShader = "<default>";
                Debug.LogWarning("[BossLaser] VFX: ningún shader de la cadena disponible — material default.");
            }
            return null;
        }

        private void ResetCast()
        {
            phase = CastPhase.Idle;
            phaseLeft = 0f;
            tickAccumulator = 0f;
            crackleAccumulator = 0f;
            if (core != null) core.enabled = false;
            if (glow != null) glow.enabled = false;
            HideTelegraph();        // [FX-F2] el aviso muere con el cast
            ApplyChargeAura(false); // restaurar PropertyBlocks del boss
        }
    }
}
