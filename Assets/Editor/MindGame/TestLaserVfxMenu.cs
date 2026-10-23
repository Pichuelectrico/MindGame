using Game.Combat;
using UnityEditor;
using UnityEngine;

namespace MindGame.Editor
{
    // [F55] Preview del rayo morado SIN jugar la partida: castea el láser
    // (recto o tornado) sobre el boss actual de la escena — o sobre un dummy
    // en el origen si no hay boss — y lo deja parpadear/girar frente a la
    // Game view. En Play mode basta con StartCast (el Update del componente
    // lo conduce); en Edit mode conducimos Tick() desde EditorApplication.update
    // con el delta real del editor (el rayo no sabe que no hay física).
    // No toca daño ni lógica: pura visual de validación.
    public static class TestLaserVfxMenu
    {
        private const string StraightMenu = "MindGame/Diagnostics/Test Laser VFX (Recto)";
        private const string TornadoMenu = "MindGame/Diagnostics/Test Laser VFX (Tornado)";
        private const float MaxPreviewSeconds = 12f; // tornado 6s + wind-up 1s + margen

        private static EditorApplication.CallbackFunction driver;
        private static double prevTime;
        private static float elapsed;
        private static bool running;
        private static BossLaserBeam previewBeam;
        private static GameObject previewDummy;
        private static Animator previewAnimator;

        [MenuItem(StraightMenu)]
        public static void TestStraight() => Run(tornado: false);

        [MenuItem(TornadoMenu)]
        public static void TestTornado() => Run(tornado: true);

        private static void Run(bool tornado)
        {
            if (running)
            {
                Debug.LogWarning("[TestLaserVFX] ya hay un preview corriendo — espera a que termine.");
                return;
            }

            // Abrir/poner en foco la Game view para que el usuario lo vea al instante.
            EditorApplication.ExecuteMenuItem("Window/General/Game");

            previewBeam = Object.FindFirstObjectByType<BossLaserBeam>();
            previewDummy = null;
            if (previewBeam != null)
            {
                // [F57] Idempotente: si el componente ya está wireado en escena
                // (SandboxBuilder.WireGruntSpawner), reutilízalo — nunca
                // duplicar ni crear un dummy innecesario.
                Debug.Log($"[TestLaserVFX] BossLaserBeam ya presente en '{previewBeam.gameObject.name}' — reutilizando.");
            }
            else
            {
                // Sin boss en escena: dummy en el origen mirando +Z (rayo hacia
                // el frente, anchor local (0, 1.5, 0.4) lo eleva a la altura de la mano).
                previewDummy = new GameObject("LaserVfxPreviewDummy");
                previewDummy.transform.SetPositionAndRotation(
                    Vector3.zero, Quaternion.LookRotation(Vector3.forward, Vector3.up));
                previewBeam = previewDummy.AddComponent<BossLaserBeam>();
                Debug.Log("[TestLaserVFX] no hay BossLaserBeam en escena — dummy creado en el origen (mirando +Z).");
            }

            var kind = tornado ? "TORNADO" : "RECTO";
            if (tornado) previewBeam.StartTornadoCast();
            else previewBeam.StartCast();
            PlayPreviewAnimation(tornado);

            running = true;
            elapsed = 0f;
            prevTime = EditorApplication.timeSinceStartup;
            driver = Drive;
            EditorApplication.update += driver;

            Debug.Log(
                Application.isPlaying
                    ? $"[TestLaserVFX] cast {kind} iniciado en PLAY mode — mira la Game view (el Update del componente lo conduce)."
                    : $"[TestLaserVFX] cast {kind} iniciado — mira la Game view: wind-up morado parpadeante ~1s y luego el rayo.");
        }

        // [F55 addendum] Reproduce el estado del controller asociado al cast
        // ("BossLaserBeam" = Combo recto, "BossLaserTornado" = PowerUp) si el
        // anfitrión tiene Animator (el boss real). Un dummy sin rig no puede
        // animar nada — se avisa en log: con boss en escena se ven ambos.
        private static void PlayPreviewAnimation(bool tornado)
        {
            previewAnimator = previewBeam.GetComponent<Animator>();
            if (previewAnimator == null)
            {
                Debug.Log("[TestLaserVFX] anfitrión sin Animator (dummy) — solo se muestra el rayo. " +
                          "Pon el boss en escena para ver también la animación " +
                          (tornado ? "SwordShield_PowerUp." : "OneHandSword_Combo."));
                return;
            }

            try
            {
                previewAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var stateName = tornado ? "BossLaserTornado" : "BossLaserBeam";
                previewAnimator.Play(stateName, 0, 0f);
                Debug.Log($"[TestLaserVFX] animación '{stateName}' en curso — mira la Game view.");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TestLaserVFX] no se pudo lanzar el estado de animación: {ex.Message} — el rayo se muestra igual.");
            }
        }

        // Conducción en edit mode: Tick con el delta real del editor. Al volver
        // a Idle (o por seguridad, a los MaxPreviewSeconds) desuscribimos y
        // limpiamos el dummy.
        private static void Drive()
        {
            var now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - prevTime);
            prevTime = now;
            elapsed += dt;

            if (previewBeam == null)
            {
                Stop();
                return;
            }

            previewBeam.Tick(dt);
            if (previewAnimator != null)
                previewAnimator.Update(dt); // avanza la animación manualmente en edit mode

            if (previewBeam.Phase == BossLaserBeam.CastPhase.Idle || elapsed > MaxPreviewSeconds)
                Stop();
        }

        private static void Stop()
        {
            if (driver != null)
            {
                EditorApplication.update -= driver;
                driver = null;
            }
            running = false;
            previewBeam = null;
            previewAnimator = null;
            if (previewDummy != null)
            {
                Object.DestroyImmediate(previewDummy); // se lleva consigo LaserCore/LaserGlow
                previewDummy = null;
            }
            Debug.Log("[TestLaserVFX] preview terminado — dummy eliminado.");
        }
    }
}
