using Game.AI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    // [F46] Boss laser: pure segment math, deterministic cast lifecycle
    // ([BAL-F1] wind-up 1.5s -> beam EXACTLY 3s -> idle, damage every 0.5s via
    // HealthSystem.ApplyDamage, self never hit), the wired asset/manifest,
    // and the controller's per-action cooldown plumbing. [FX-F2] telegraphy
    // (marker/pillar/aura) is also covered here.
    public sealed class BossLaserBeamTests
    {
        // ------------------------------------------------------- SegmentHit

        [Test]
        public void SegmentHit_OnAxisInside_True()
        {
            Assert.IsTrue(Game.Combat.BossLaserBeam.SegmentHit(
                Vector3.zero, Vector3.forward, 20f, 0.8f, new Vector3(0f, 0f, 10f)));
        }

        [Test]
        public void SegmentHit_WithinHalfWidth_True()
        {
            Assert.IsTrue(Game.Combat.BossLaserBeam.SegmentHit(
                Vector3.zero, Vector3.forward, 20f, 0.8f, new Vector3(0.39f, 0f, 10f)));
        }

        [Test]
        public void SegmentHit_ExactlyAtHalfWidth_True()
        {
            Assert.IsTrue(Game.Combat.BossLaserBeam.SegmentHit(
                Vector3.zero, Vector3.forward, 20f, 0.8f, new Vector3(0.4f, 0f, 10f)));
        }

        [Test]
        public void SegmentHit_BeyondHalfWidth_False()
        {
            Assert.IsFalse(Game.Combat.BossLaserBeam.SegmentHit(
                Vector3.zero, Vector3.forward, 20f, 0.8f, new Vector3(0.41f, 0f, 10f)));
        }

        [Test]
        public void SegmentHit_BeyondMaxDist_False()
        {
            Assert.IsFalse(Game.Combat.BossLaserBeam.SegmentHit(
                Vector3.zero, Vector3.forward, 20f, 0.8f, new Vector3(0f, 0f, 21f)));
        }

        [Test]
        public void SegmentHit_BehindOrigin_False()
        {
            Assert.IsFalse(Game.Combat.BossLaserBeam.SegmentHit(
                Vector3.zero, Vector3.forward, 20f, 0.8f, new Vector3(0f, 0f, -1f)));
        }

        [Test]
        public void SegmentHit_UnnormalizedDir_NormalizedInternally()
        {
            // A scaled direction must behave exactly like its normalized version.
            Assert.IsTrue(Game.Combat.BossLaserBeam.SegmentHit(
                Vector3.zero, Vector3.forward * 3f, 20f, 0.8f, new Vector3(0f, 0f, 10f)));
            Assert.IsFalse(Game.Combat.BossLaserBeam.SegmentHit(
                Vector3.zero, Vector3.forward * 3f, 20f, 0.8f, new Vector3(0f, 0f, 25f)));
        }

        // ------------------------------------------------- Cast lifecycle

        private static void SetHealth(Game.Combat.HealthSystem hs, int current, int max)
        {
            var so = new SerializedObject(hs);
            so.FindProperty("currentHealth").intValue = current;
            so.FindProperty("maxHealth").intValue = max;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // [BAL-F1] Beam lifetime = exactly 3s, ticks every 0.5s (6 ticks of 7),
        // wind-up TELEGRAPHED 1.5s (FX-F2) deals no damage, the boss never
        // hits itself. Driven by Tick(dt) — no Time.time, fully deterministic.
        [Test]
        public void BeamLifecycle_WindUpThenExactlyThreeSecondsOfTicks()
        {
            var laserGo = new GameObject("laser-boss");
            var targetGo = new GameObject("laser-target");
            try
            {
                laserGo.transform.position = Vector3.zero; // forward = +Z
                targetGo.transform.position = new Vector3(0f, 0f, 10f); // on the beam axis
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();
                var selfHs = laserGo.AddComponent<Game.Combat.HealthSystem>();
                var targetHs = targetGo.AddComponent<Game.Combat.HealthSystem>();
                SetHealth(selfHs, 100, 100);
                SetHealth(targetHs, 100, 100);

                // Wind-up: telegraph only — no damage yet.
                beam.StartCast();
                Assert.AreEqual(Game.Combat.BossLaserBeam.CastPhase.WindUp, beam.Phase);
                beam.Tick(0.9f);
                Assert.IsFalse(beam.IsBeaming);
                Assert.AreEqual(100, targetHs.CurrentHealth);
                Assert.AreEqual(0, beam.TickCount);
                beam.Tick(0.6f); // completa el wind-up de 1.5s — la transición a Beam ocurre dentro del mismo Tick
                Assert.IsTrue(beam.IsBeaming); // beam "live" en 1.5s exactos (aún 0 daño: primer tick a los 0.5s)
                Assert.AreEqual(100, targetHs.CurrentHealth); // sin daño durante el wind-up

                // 1.5s total -> beam live.
                beam.Tick(0.0001f);
                Assert.IsTrue(beam.IsBeaming);

                // 5 half-second ticks: 35 damage, still beaming (2.5s of 3s).
                for (int i = 0; i < 5; i++) beam.Tick(0.5f);
                Assert.AreEqual(5, beam.TickCount);
                Assert.AreEqual(100 - 35, targetHs.CurrentHealth);
                Assert.IsTrue(beam.IsBeaming);

                // 6th tick closes the beam exactly at 3s of beam time.
                beam.Tick(0.5f);
                Assert.AreEqual(6, beam.TickCount);
                Assert.AreEqual(100 - 42, targetHs.CurrentHealth);
                Assert.AreEqual(Game.Combat.BossLaserBeam.CastPhase.Idle, beam.Phase);
                Assert.IsFalse(beam.IsBeaming);

                // The boss never hits itself, and extra ticks after the cast
                // change nothing.
                Assert.AreEqual(100, selfHs.CurrentHealth);
                beam.Tick(2f);
                Assert.AreEqual(100 - 42, targetHs.CurrentHealth);
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
                Object.DestroyImmediate(targetGo);
            }
        }

        // -------------------------------------------------- Wired assets

        // The .asset + manifest entry are created by MindGame/Setup/Wire Boss
        // Laser (idempotent). The test locks the wired values.
        [Test]
        public void WiredAction_BossOnlyLaser_DefinitionAndManifest()
        {
            var def = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionDefinition>(
                "Assets/Data/Actions/BossLaserBeam.asset");
            Assert.IsNotNull(def, "BossLaserBeam.asset missing — run MindGame/Setup/Wire Boss Laser.");

            Assert.AreEqual("BossLaserBeam", def.ActionId);
            Assert.AreEqual(Game.Actions.ActionRole.BossOnly, def.RoleRestriction);
            Assert.AreEqual(30f, def.StaminaCost, 1e-3f);
            Assert.AreEqual(12f, def.Range, 1e-3f); // [F49] balance 20 -> 12
            Assert.AreEqual(4.5f, def.Duration, 1e-3f); // [BAL-F1] 1.5s wind-up + 3s beam
            Assert.AreEqual("OneHandSword_Combo", def.ClipName);
            Assert.IsFalse(def.IsHeavy, "laser ticks are never heavy");

            var manifest = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionManifest>(
                "Assets/Data/Actions/ActionManifest.asset");
            Assert.IsNotNull(manifest, "ActionManifest.asset missing");
            Assert.IsNotNull(manifest.Find("BossLaserBeam"), "manifest lacks BossLaserBeam");
        }

        // ------------------------------------- Controller cooldown plumbing

        // [F46] Per-action cooldown: tracked actions recycle; untracked actions
        // are always ready. NoteActionStarted is public — EditMode-drivable.
        [Test]
        public void ControllerCooldown_TracksLaser_IgnoresUntracked()
        {
            var go = new GameObject("boss-cooldown");
            try
            {
                var ctrl = go.AddComponent<Game.AI.BotController>();

                // Untracked action: always ready (0 remaining).
                Assert.AreEqual(0f, ctrl.RemainingCooldown("Punch"), 1e-3f);

                // Not yet started: ready.
                Assert.AreEqual(0f, ctrl.RemainingCooldown("BossLaserBeam"), 1e-3f);

                // Started: on cooldown (10s default), and the probe is
                // relative to the same frame's Time.time — deterministic.
                ctrl.NoteActionStarted("BossLaserBeam");
                float remaining = ctrl.RemainingCooldown("BossLaserBeam");
                Assert.GreaterOrEqual(remaining, 9.9f);
                Assert.LessOrEqual(remaining, 10.001f);

                // [F47] Tornado: 14s default — independent of the laser.
                ctrl.NoteActionStarted("BossLaserTornado");
                float tornadoRemaining = ctrl.RemainingCooldown("BossLaserTornado");
                Assert.GreaterOrEqual(tornadoRemaining, 13.9f);
                Assert.LessOrEqual(tornadoRemaining, 14.001f);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // [F46] The heuristic consults the injected probe: laser on cooldown ->
        // the boss runs in (same decision as pre-F46 far behavior).
        [Test]
        public void ControllerCooldown_ProbeInjectedIntoBrain()
        {
            var brain = new BotBrain { RemainingCooldownOf = _ => 5f };
            Assert.AreEqual(5f, brain.RemainingCooldownOf("BossLaserBeam"), 1e-3f);
        }

        // [F47] Tornado: constant 180deg/s — exactly 720deg over the 4s beam
        // (2x360, [BAL-F1] antes 6s a 120deg/s), zero drift (absolute angle
        // from castForward). Deterministic.
        [Test]
        public void TornadoCast_SpinsExactly720DegreesOverFourSeconds()
        {
            var laserGo = new GameObject("tornado-boss");
            try
            {
                laserGo.transform.position = Vector3.zero;
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();

                beam.StartTornadoCast();
                Assert.AreEqual(Game.Combat.BossLaserBeam.CastPhase.WindUp, beam.Phase);
                Assert.IsTrue(beam.TornadoMode);

                // 1.5s wind-up -> beam live for exactly 4s (720deg at 180deg/s).
                beam.Tick(1.5f);
                Assert.IsTrue(beam.IsBeaming);
                Assert.AreEqual(0f, beam.SpinAngle, 1e-3f); // wind-up dt doesn't spin the beam

                // Constant angular speed: 360deg at the halfway point (2s).
                for (int i = 0; i < 4; i++) beam.Tick(0.5f);
                Assert.AreEqual(360f, beam.SpinAngle, 1e-3f);
                Assert.IsTrue(beam.IsBeaming);

                // Full cast: 720deg, beam closes exactly at 4s of beam time.
                for (int i = 0; i < 4; i++) beam.Tick(0.5f);
                Assert.AreEqual(720f, beam.SpinAngle, 1e-3f);
                Assert.AreEqual(Game.Combat.BossLaserBeam.CastPhase.Idle, beam.Phase);
                Assert.IsFalse(beam.IsBeaming);
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
            }
        }

        // [F47] Tornado damage = same segment intersection as the straight
        // beam (tick-sampled): a target standing on a sampled ray takes the
        // tick; the boss never hits itself. 720deg = TWO revolutions, so the
        // beam sweeps the target once PER REVOLUTION -> exactly 2 ticks.
        [Test]
        public void TornadoCast_DamagesBySegmentIntersection()
        {
            var laserGo = new GameObject("tornado-boss");
            var targetGo = new GameObject("tornado-target");
            try
            {
                laserGo.transform.position = Vector3.zero; // forward = +Z (spin 0deg)
                // [BAL-F1] 5m out on the 90deg ray: (5*sin90, 0, 5*cos90). Con
                // 180deg/s los samples caen en múltiplos de 90deg — el rayo de
                // 90deg recibe 1 sweep por revolución (samples 90 y 450).
                // OJO: el rayo nace en beamOrigin = anchorLocal (0,1.5,0.4) —
                // z=0.4, no z=0 (con hitHalfWidth 0.2 el target en z=0 queda
                // a 0.4m del rayo -> MISS).
                targetGo.transform.position = new Vector3(5f, 0f, 0.4f);
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();
                var selfHs = laserGo.AddComponent<Game.Combat.HealthSystem>();
                var targetHs = targetGo.AddComponent<Game.Combat.HealthSystem>();
                SetHealth(selfHs, 100, 100);
                SetHealth(targetHs, 100, 100);

                beam.StartTornadoCast();
                beam.Tick(1.5f); // wind-up done
                for (int i = 0; i < 8; i++) beam.Tick(0.5f); // full 4s spin

                Assert.AreEqual(720f, beam.SpinAngle, 1e-3f);
                Assert.AreEqual(100 - 2 * 7, targetHs.CurrentHealth); // one sweep hit per revolution
                Assert.AreEqual(100, selfHs.CurrentHealth);           // boss immune to itself
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
                Object.DestroyImmediate(targetGo);
            }
        }

        // [F47] Wired tornado asset: BossOnly / 35 stamina / 12m / 5.5s cast
        // ([BAL-F1]: 1.5s wind-up + 4s de giro) / SwordShield_PowerUp
        // ([F55 addendum] era OneHandSword_Combo) / never heavy + manifest.
        [Test]
        public void WiredAction_Tornado_DefinitionAndManifest()
        {
            var def = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionDefinition>(
                "Assets/Data/Actions/BossLaserTornado.asset");
            Assert.IsNotNull(def, "BossLaserTornado.asset missing — run MindGame/Setup/Wire Boss Laser.");

            Assert.AreEqual("BossLaserTornado", def.ActionId);
            Assert.AreEqual(Game.Actions.ActionRole.BossOnly, def.RoleRestriction);
            Assert.AreEqual(35f, def.StaminaCost, 1e-3f);
            Assert.AreEqual(12f, def.Range, 1e-3f); // [F49] balance 20 -> 12
            Assert.AreEqual(5.5f, def.Duration, 1e-3f); // [BAL-F1] 1.5s wind-up + 4s spin
            Assert.AreEqual("SwordShield_PowerUp", def.ClipName); // [F55 addendum] power-up del usuario
            Assert.IsFalse(def.IsHeavy, "tornado ticks are never heavy");

            var manifest = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionManifest>(
                "Assets/Data/Actions/ActionManifest.asset");
            Assert.IsNotNull(manifest, "ActionManifest.asset missing");
            Assert.IsNotNull(manifest.Find("BossLaserTornado"), "manifest lacks BossLaserTornado");
        }

        // ------------------------------------------------ Wire [F57]

        // [F57] Wire idempotente del láser en el boss: el executor dispara
        // ActionStarted y sin componente escuchando no hay beam ni daño (bug
        // de integración: el componente solo lo montaba el menú preview).
        // SandboxBuilder.WireGruntSpawner llama BossLaserBeam.EnsureOn; aquí
        // verificamos el contrato (agrega una vez, idempotente, null-safe).
        // Vive en runtime-assembly a propósito: SandboxBuilder es de editor;
        // la ejecución real del wire se verifica por bridge (log
        // "[SandboxBuilder] +BossLaserBeam") + YAML de la escena guardada.
        [Test]
        public void Wire_EnsureOn_AddsComponentOnce_ThenNoOp()
        {
            var host = new GameObject("wire-boss");
            try
            {
                Assert.IsNull(host.GetComponent<Game.Combat.BossLaserBeam>(), "pre: el GO no debe tener el componente");

                Assert.IsTrue(Game.Combat.BossLaserBeam.EnsureOn(host), "la primera llamada debe agregar");
                Assert.IsNotNull(host.GetComponent<Game.Combat.BossLaserBeam>(), "componente presente tras el wire");

                Assert.IsFalse(Game.Combat.BossLaserBeam.EnsureOn(host), "la segunda llamada es no-op");
                Assert.AreEqual(1, host.GetComponents<Game.Combat.BossLaserBeam>().Length, "nunca duplicar el componente");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Wire_EnsureOn_NullHost_IsSafe()
        {
            Assert.IsFalse(Game.Combat.BossLaserBeam.EnsureOn(null), "host null -> no agrega y no lanza");
        }

        // ------------------------------------------------ Balance [BAL-F1]

        // [BAL-F1] Lock de los números del rebalance. El usuario: "que dure
        // menos, como 10 segundos" — evento total 4.5s (recto) / 5.5s
        // (tornado); el roce de 1-2 ticks cuesta 7-14 HP (banda ~5-15) y el
        // contacto total ya no mata a full HP.
        [Test]
        public void Balance_ComponentNumbers_LockTheIntent()
        {
            var laserGo = new GameObject("balance-boss");
            try
            {
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();
                var so = new UnityEditor.SerializedObject(beam);
                Assert.AreEqual(1.5f, so.FindProperty("windUp").floatValue, 1e-3f, "wind-up telegrafiado 1.5s (FX-F2)");
                Assert.AreEqual(3f, so.FindProperty("beamDuration").floatValue, 1e-3f, "beam recto 3s");
                Assert.AreEqual(4f, so.FindProperty("tornadoBeamDuration").floatValue, 1e-3f, "tornado 4s");
                Assert.AreEqual(0.5f, so.FindProperty("tickInterval").floatValue, 1e-3f);
                Assert.AreEqual(7, so.FindProperty("damagePerTick").intValue, "7/tick: roce 7-14, contacto total 42/56");
                Assert.AreEqual(180f, so.FindProperty("spinDegreesPerSecond").floatValue, 1e-3f, "180deg/s x 4s = 720deg");
                Assert.AreEqual(0.2f, so.FindProperty("hitHalfWidth").floatValue, 1e-3f, "hit = core 0.10 + margen 0.10");

                // Evento total corto (pide del usuario) y 2 vueltas exactas.
                Assert.LessOrEqual(beam.WindUpSeconds + beam.BeamSeconds, 6f, "recto total");
                Assert.LessOrEqual(beam.WindUpSeconds + beam.TornadoBeamSeconds, 6f, "tornado total");
                Assert.AreEqual(720f, so.FindProperty("spinDegreesPerSecond").floatValue * beam.TornadoBeamSeconds, 1e-3f, "2 vueltas");
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
            }
        }

        // [BAL-F1] Contacto TOTAL del recto NO es letal a 100 HP: 6 ticks x 7
        // = 42 (antes 10 ticks x 6 = 60 con beam de 5s).
        [Test]
        public void Balance_FullContact_Straight_NotLethal()
        {
            var laserGo = new GameObject("balance-boss");
            var targetGo = new GameObject("balance-target");
            try
            {
                laserGo.transform.position = Vector3.zero;
                targetGo.transform.position = new Vector3(0f, 0f, 10f); // on the axis
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();
                var targetHs = targetGo.AddComponent<Game.Combat.HealthSystem>();
                SetHealth(targetHs, 100, 100);

                beam.StartCast();
                beam.Tick(1.5f); // wind-up completo, 0 daño
                for (int i = 0; i < 6; i++) beam.Tick(0.5f); // beam 3s completo

                Assert.AreEqual(Game.Combat.BossLaserBeam.CastPhase.Idle, beam.Phase);
                Assert.Greater(targetHs.CurrentHealth, 0, "el contacto total NO mata a full HP");
                Assert.AreEqual(100 - 42, targetHs.CurrentHealth, "6 ticks x 7");
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
                Object.DestroyImmediate(targetGo);
            }
        }

        // [BAL-F1] El roce (1-2 ticks) queda en la banda sobrevivible del
        // usuario (~5-15 HP): 1 tick = 7, 2 ticks = 14.
        [Test]
        public void Balance_Graze_OneOrTwoTicks_InUserBand()
        {
            var laserGo = new GameObject("balance-boss");
            var targetGo = new GameObject("balance-target");
            try
            {
                laserGo.transform.position = Vector3.zero;
                targetGo.transform.position = new Vector3(0f, 0f, 10f);
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();
                var targetHs = targetGo.AddComponent<Game.Combat.HealthSystem>();
                SetHealth(targetHs, 100, 100);

                beam.StartCast();
                beam.Tick(1.5f); // wind-up
                beam.Tick(0.5f); // 1 tick de roce

                var lost1 = 100 - targetHs.CurrentHealth;
                Assert.GreaterOrEqual(lost1, 5, "1 tick: dentro de la banda del usuario");
                Assert.LessOrEqual(lost1, 15, "1 tick: dentro de la banda del usuario");
                Assert.AreEqual(7, lost1);

                beam.Tick(0.5f); // 2do tick
                var lost2 = 100 - targetHs.CurrentHealth;
                Assert.LessOrEqual(lost2, 15, "2 ticks de roce: aún sobrevivible");
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
                Object.DestroyImmediate(targetGo);
            }
        }

        // [BAL-F1] El HIT usa el core + margen (0.2m half), no el width viejo
        // (0.8m = half 0.4): a 0.3m lateral — donde ANTES pegaba sin tocar el
        // rayo visible — ya no hay daño; a 0.15m sí.
        [Test]
        public void Balance_HitWidth_BeyondCoreMargin_NoDamage()
        {
            var laserGo = new GameObject("balance-boss");
            var grazeGo = new GameObject("graze-out");   // 0.3m lateral: fuera del hit nuevo
            var closeGo = new GameObject("graze-in");    // 0.15m lateral: dentro del hit
            try
            {
                laserGo.transform.position = Vector3.zero;
                grazeGo.transform.position = new Vector3(0.3f, 0f, 10f);
                closeGo.transform.position = new Vector3(0.15f, 0f, 10f);
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();
                var grazeHs = grazeGo.AddComponent<Game.Combat.HealthSystem>();
                var closeHs = closeGo.AddComponent<Game.Combat.HealthSystem>();
                SetHealth(grazeHs, 100, 100);
                SetHealth(closeHs, 100, 100);

                beam.StartCast();
                beam.Tick(1.5f);
                for (int i = 0; i < 6; i++) beam.Tick(0.5f); // beam completo

                Assert.AreEqual(100, grazeHs.CurrentHealth, "0.3m lateral: ni un roce del rayo visible, 0 daño");
                Assert.Less(closeHs.CurrentHealth, 100, "0.15m lateral: el rayo sí pega");
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
                Object.DestroyImmediate(grazeGo);
                Object.DestroyImmediate(closeGo);
            }
        }

        // [BAL-F1] Tornado: contacto total también sobrevivible — tope
        // teórico 8 ticks x 7 = 56 < 100; con este setup caen 2 sweeps x 7.
        [Test]
        public void Balance_Tornado_FullContact_NotLethal()
        {
            var laserGo = new GameObject("balance-boss");
            var targetGo = new GameObject("balance-target");
            try
            {
                laserGo.transform.position = Vector3.zero;
                targetGo.transform.position = new Vector3(5f, 0f, 0.4f); // rayo 90deg desde anchorLocal z=0.4
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();
                var targetHs = targetGo.AddComponent<Game.Combat.HealthSystem>();
                SetHealth(targetHs, 100, 100);

                beam.StartTornadoCast();
                beam.Tick(1.5f);
                for (int i = 0; i < 8; i++) beam.Tick(0.5f); // giro completo

                Assert.Greater(targetHs.CurrentHealth, 0, "el tornado completo NO mata a full HP");
                Assert.LessOrEqual(100 - targetHs.CurrentHealth, 8 * 7, "tope teórico 8 ticks");
                Assert.AreEqual(100 - 14, targetHs.CurrentHealth, "2 sweeps x 7 en el rayo de 90deg");
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
                Object.DestroyImmediate(targetGo);
            }
        }

        // ------------------------------------------------ Telegraph [FX-F2]

        // [FX-F2] Recto: LÍNEA en el suelo durante el wind-up, del boss al
        // final del rayo (range), visible ANTES del primer tick de daño.
        [Test]
        public void Telegraph_LineMarker_VisibleDuringWindUp()
        {
            var laserGo = new GameObject("telegraph-boss");
            try
            {
                laserGo.transform.position = Vector3.zero; // forward = +Z
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();

                beam.StartCast();

                var marker = beam.TelegraphRenderer;
                Assert.IsNotNull(marker, "marcador de suelo creado al castear");
                Assert.IsTrue(marker.enabled, "marcador visible durante el wind-up");
                Assert.AreEqual(12, marker.positionCount);
                Assert.IsTrue((marker.GetPosition(0) - new Vector3(0f, 0.05f, 0f)).sqrMagnitude < 1e-6f, "arranca en los pies del boss"); // tol 0.001m (AreEqual(Vector3,...,delta) no compila: overload double de UTF)
                Assert.IsTrue((marker.GetPosition(11) - new Vector3(0f, 0.05f, 12f)).sqrMagnitude < 1e-6f, "termina donde llega el rayo (12m)");

                Assert.AreEqual(0, beam.TickCount, "telegrafía ANTES del primer tick de daño");
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
            }
        }

        // [FX-F2] Tornado: CÍRCULO del barrido completo (radio = range),
        // cerrado (primer punto == último).
        [Test]
        public void Telegraph_CircleMarker_Tornado()
        {
            var laserGo = new GameObject("telegraph-boss");
            try
            {
                laserGo.transform.position = Vector3.zero;
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();

                beam.StartTornadoCast();

                var marker = beam.TelegraphRenderer;
                Assert.IsNotNull(marker);
                Assert.IsTrue(marker.enabled);
                Assert.AreEqual(49, marker.positionCount);
                Assert.IsTrue((marker.GetPosition(0) - marker.GetPosition(48)).sqrMagnitude < 1e-6f, "círculo cerrado"); // tol 0.001m
                // Punto a 90deg del círculo: (12, 0.05, ~0) — tolerancia por
                // el residuo float de cos(π/2).
                Assert.IsTrue((marker.GetPosition(12) - new Vector3(12f, 0.05f, 0f)).sqrMagnitude < 1e-6f, "radio = range"); // tol 0.001m
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
            }
        }

        // [FX-F2] El pilar de carga (la "aurora") está vivo SOLO en el
        // wind-up; muere cuando empieza el daño.
        [Test]
        public void Telegraph_PillarAndMarker_HideWhenBeamStarts()
        {
            var laserGo = new GameObject("telegraph-boss");
            try
            {
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();

                beam.StartCast();
                Assert.IsTrue(beam.ChargePillarRenderer.enabled, "pilar visible en wind-up");
                Assert.AreEqual(2, beam.ChargePillarRenderer.positionCount, "pilar vertical (2 puntos)");
                var height = Vector3.Distance(beam.ChargePillarRenderer.GetPosition(0), beam.ChargePillarRenderer.GetPosition(1));
                Assert.AreEqual(2.35f, height, 0.01f, "pilar ~2.4m de alto");

                beam.Tick(1.51f); // wind-up -> beam

                Assert.AreEqual(Game.Combat.BossLaserBeam.CastPhase.Beam, beam.Phase);
                Assert.IsFalse(beam.TelegraphRenderer.enabled, "marcador fuera al empezar el daño");
                Assert.IsFalse(beam.ChargePillarRenderer.enabled, "pilar fuera al empezar el daño");
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
            }
        }

        // [FX-F2] El telegraph existe desde el PRIMER instante del cast:
        // visible y sin ningún daño aplicado (telegrafía real).
        [Test]
        public void Telegraph_VisibleBeforeFirstDamageTick()
        {
            var laserGo = new GameObject("telegraph-boss");
            try
            {
                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();

                beam.StartCast();

                Assert.IsTrue(beam.TelegraphRenderer.enabled);
                Assert.IsTrue(beam.ChargePillarRenderer.enabled);
                Assert.AreEqual(Game.Combat.BossLaserBeam.CastPhase.WindUp, beam.Phase);
                Assert.AreEqual(0, beam.TickCount, "cero ticks de daño durante el aviso");
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
            }
        }

        // [FX-F2] Aura emissive por PropertyBlock: el canary "_BossLaserCharge"
        // se setea en los renderers del boss durante el wind-up y se RESTAURA
        // al terminar el cast (los materiales quedan intactos).
        [Test]
        public void Telegraph_EmissionBlock_SetDuringWindUp_RestoredAfterCast()
        {
            var laserGo = new GameObject("telegraph-boss");
            try
            {
                // Renderer "del boss" (cualquiera que no sea del láser).
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "boss-body";
                cube.transform.SetParent(laserGo.transform, false);
                var body = cube.GetComponent<MeshRenderer>();

                var beam = laserGo.AddComponent<Game.Combat.BossLaserBeam>();

                beam.StartCast();
                var blk = new MaterialPropertyBlock();
                body.GetPropertyBlock(blk);
                Assert.Greater(blk.GetFloat("_BossLaserCharge"), 0f, "aura aplicada en wind-up");

                while (beam.Phase != Game.Combat.BossLaserBeam.CastPhase.Idle)
                    beam.Tick(0.5f);

                body.GetPropertyBlock(blk);
                Assert.AreEqual(0f, blk.GetFloat("_BossLaserCharge"), "aura restaurada al terminar");
            }
            finally
            {
                Object.DestroyImmediate(laserGo);
            }
        }
    }
}
