using Game.AI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    // [F55] Rayo morado de rayos: capa visual del BossLaserBeam. Verifica que
    // el cast crea los DOS renderers hijos (core + glow), con positionCount
    // == segmentos+1, jitter re-randomizable de forma determinista (mismo
    // CrackleSeed -> mismo rayo; endpoints clavados), que la cadena de
    // shaders cae al fallback cuando el primero no existe (ShaderResolver
    // inyectable), y que los colores morados HDR quedan aplicados. Todo en
    // EditMode: AddComponent no corre Awake, así que EnsureVfx lazy dentro
    // de ShowBolt es el camino que ejercitan estos tests (igual que en juego
    // la primera vez). NO se toca daño/ticks: eso vive en BossLaserBeamTests.
    public sealed class BossLaserVfxTests
    {
        private GameObject go;
        private Game.Combat.BossLaserBeam beam;

        [SetUp]
        public void SetUp()
        {
            go = new GameObject("laser-vfx-boss");
            go.transform.position = Vector3.zero; // forward = +Z
            beam = go.AddComponent<Game.Combat.BossLaserBeam>();
            beam.CrackleSeed = 12345u; // determinismo: mismo seed -> mismo rayo
        }

        [TearDown]
        public void TearDown()
        {
            // Los materiales de la cadena se crean con HideFlags.DontSave y el
            // resolver estático SIEMPRE se restaura (otros tests usan el real).
            if (go != null) Object.DestroyImmediate(go);
        }

        // ------------------------------------------------- 2 renderers al cast

        [Test]
        public void Vfx_StartCast_CreatesTwoEnabledChildRenderers()
        {
            beam.StartCast(); // wind-up: ShowBolt -> EnsureVfx lazy (sin Awake)

            var core = beam.CoreRenderer;
            var glow = beam.GlowRenderer;
            Assert.IsNotNull(core, "LaserCore no creado al castear.");
            Assert.IsNotNull(glow, "LaserGlow no creado al castear.");
            Assert.AreEqual("LaserCore", core.gameObject.name);
            Assert.AreEqual("LaserGlow", glow.gameObject.name);
            Assert.AreEqual(go.transform, core.transform.parent, "LaserCore debe ser hijo del beam.");
            Assert.AreEqual(go.transform, glow.transform.parent, "LaserGlow debe ser hijo del beam.");
            Assert.IsTrue(core.enabled, "LaserCore disabled al iniciar el cast.");
            Assert.IsTrue(glow.enabled, "LaserGlow disabled al iniciar el cast.");
            // sharedMaterial: leer .material en edit mode instancia una copia
            // y Unity la loguea como error (falso negativo del test).
            Assert.IsNotNull(core.sharedMaterial, "LaserCore sin material (cadena de shaders falló en silencio).");
            Assert.IsNotNull(glow.sharedMaterial, "LaserGlow sin material.");
        }

        [Test]
        public void Vfx_PositionCount_EqualsSegmentsPlusOne()
        {
            beam.StartCast();

            int expected = beam.LightningSegments + 1; // 16 segmentos -> 17 puntos
            Assert.AreEqual(expected, beam.CoreRenderer.positionCount);
            Assert.AreEqual(expected, beam.GlowRenderer.positionCount);
        }

        // --------------------------------- re-randomización determinista (crackle)

        [Test]
        public void Vfx_RerollJitter_FixedSeed_ChangesMiddle_KeepsEndpoints()
        {
            beam.StartCast();
            var core = beam.CoreRenderer;

            int last = beam.LightningSegments;
            var start0 = core.GetPosition(0);
            var startMid = core.GetPosition(5);
            var startEnd = core.GetPosition(last);

            // Nuevo seed -> nuevo zigzag (en juego lo hace el crackle cada ~0.1s).
            beam.CrackleSeed = 67890u;
            beam.RerollJitter();
            beam.ApplyPoints();

            var end0 = core.GetPosition(0);
            var endMid = core.GetPosition(5);
            var endEnd = core.GetPosition(last);

            Assert.AreEqual(start0, end0, "El endpoint de origen NO debe moverse con el jitter.");
            Assert.AreEqual(startEnd, endEnd, "El endpoint final NO debe moverse con el jitter.");
            Assert.Greater(Vector3.Distance(startMid, endMid), 0.0001f,
                "Con seed distinto la posición media debe cambiar (re-randomización).");

            // Sanity: el rayo cubre ~range (12m) del origen al final.
            Assert.Greater(Vector3.Distance(start0, startEnd), 11f);
        }

        [Test]
        public void Vfx_RerollJitter_SameSeed_Reproducible()
        {
            beam.StartCast(); // crea renderers + primer ApplyPoints

            // Mismo seed DOS veces -> exactamente el mismo rayo (determinismo
            // requerido para test y para que el crackle no sea caótico).
            beam.CrackleSeed = 999u;
            beam.RerollJitter();
            beam.ApplyPoints();
            var first = beam.CoreRenderer.GetPosition(7);

            beam.RerollJitter(); // seed intacto
            beam.ApplyPoints();
            var second = beam.CoreRenderer.GetPosition(7);

            Assert.AreEqual(first, second);
        }

        // ---------------------------------------------------- cadena de shaders

        [Test]
        public void Vfx_ShaderChain_FallsBack_WhenFirstShaderMissing()
        {
            var original = Game.Combat.BossLaserBeam.ShaderResolver;
            try
            {
                // El primero de la cadena (URP Particles/Unlit) "no existe":
                // la cadena debe caer al siguiente (Sprites/Default) sin dejar
                // material null — este era el fallo silencioso del rayo viejo.
                Game.Combat.BossLaserBeam.ShaderResolver = name =>
                    name == "Universal Render Pipeline/Particles/Unlit" ? null : Shader.Find(name);

                beam.StartCast();

                var core = beam.CoreRenderer;
                Assert.IsNotNull(core.sharedMaterial, "Fallback debió dejar material, no null.");
                Assert.AreEqual("Sprites/Default", core.sharedMaterial.shader.name,
                    "Con URP/Particles/Unlit ausente, el material debe usar Sprites/Default.");
            }
            finally
            {
                Game.Combat.BossLaserBeam.ShaderResolver = original;
            }
        }

        [Test]
        public void Vfx_ShaderChain_AllNull_WarnsAndKeepsRendering()
        {
            var original = Game.Combat.BossLaserBeam.ShaderResolver;
            try
            {
                Game.Combat.BossLaserBeam.ShaderResolver = _ => null;

                // Sin NINGÚN shader disponible no existe material construible:
                // el componente debe avisar (warning, no error) y seguir
                // renderizando con el default de Unity — nunca lanzar ni
                // dejar apagado.
                beam.StartCast();

                Assert.IsTrue(beam.CoreRenderer.enabled, "El core sigue habilitado (render con default).");
                Assert.IsTrue(beam.GlowRenderer.enabled, "El glow sigue habilitado.");
                Assert.AreEqual(Game.Combat.BossLaserBeam.CastPhase.WindUp, beam.Phase,
                    "El cast arrancó pese a la cadena rota.");
            }
            finally
            {
                Game.Combat.BossLaserBeam.ShaderResolver = original;
            }
        }

        // ------------------------------------------------------- color morado

        [Test]
        public void Vfx_WindUp_UsesDimFlickerColor()
        {
            beam.StartCast();

            var c = beam.CoreRenderer.startColor;
            // Color32 trunca a 8 bits: comparar con tolerancia, no exacto.
            Assert.That(c.r, Is.EqualTo(beam.WindUpColor.r).Within(0.02f), "canal R del wind-up");
            Assert.That(c.g, Is.EqualTo(beam.WindUpColor.g).Within(0.02f), "canal G del wind-up");
            Assert.That(c.b, Is.EqualTo(beam.WindUpColor.b).Within(0.02f), "canal B del wind-up");
            Assert.That(c.a, Is.EqualTo(beam.WindUpColor.a).Within(0.02f), "canal A del wind-up");
            Assert.Less(c.a, 1f, "Wind-up translúcido.");
        }

        // Extrae el color del material por la propiedad que exista (misma
        // prioridad que ApplyHdrColor del componente).
        private static Color MaterialColor(Material m)
        {
            if (m == null) return Color.clear;
            if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
            if (m.HasProperty("_Color")) return m.GetColor("_Color");
            if (m.HasProperty("_TintColor")) return m.GetColor("_TintColor");
            return Color.clear;
        }

        [Test]
        public void Vfx_BeamPhase_AppliesPurpleHdrColors()
        {
            beam.StartCast();
            beam.Tick(1.51f); // consume el wind-up (1.5s, [BAL-F1]) -> transición a Beam

            // El HDR real (>1) vive en el COLOR DEL MATERIAL: los vertex colors
            // del LineRenderer son Color32 y se truncan a [0,1] en silencio
            // (lección F55: por eso el rayo viejo se veía apagado/desaturado).
            var coreMat = MaterialColor(beam.CoreRenderer.sharedMaterial);
            var glowMat = MaterialColor(beam.GlowRenderer.sharedMaterial);
            Assert.Greater(coreMat.r, 1f, "Core: HDR real en rojo vía material (>1).");
            Assert.Greater(coreMat.b, 2f, "Core: HDR real en azul/morado vía material (>2).");
            Assert.Greater(glowMat.r, 1f, "Glow: HDR real vía material.");
            Assert.Less(glowMat.a, 1f, "Glow translúcido (alpha < 1 en material).");

            // En vertex colors (truncados) queda el TINT morado: azul > rojo > verde.
            var core = beam.CoreRenderer.startColor;
            Assert.Greater(core.b, core.g, "Morado: azul > verde.");
            Assert.GreaterOrEqual(core.r, core.g, "Morado: rojo >= verde.");
            var glow = beam.GlowRenderer.startColor;
            Assert.Greater(glow.b, glow.g, "Glow morado: azul > verde.");
            Assert.AreEqual(beam.GlowColor.a, glow.a, 0.02f, "Alpha del glow en vertex color.");
        }

        [Test]
        public void Vfx_ResetCast_DisablesRenderers()
        {
            beam.StartCast();
            Assert.IsTrue(beam.CoreRenderer.enabled);

            // Ticks chicos (como el juego): un solo Tick(20) entra al return
            // del damage-tick sin revisar el fin de beam. Loop con guard.
            int guard = 0;
            while (beam.Phase != Game.Combat.BossLaserBeam.CastPhase.Idle && guard++ < 100)
                beam.Tick(0.5f);

            Assert.AreEqual(Game.Combat.BossLaserBeam.CastPhase.Idle, beam.Phase);
            Assert.IsFalse(beam.CoreRenderer.enabled, "Al volver a Idle el core debe apagarse.");
            Assert.IsFalse(beam.GlowRenderer.enabled, "Al volver a Idle el glow debe apagarse.");
        }
    }
}
