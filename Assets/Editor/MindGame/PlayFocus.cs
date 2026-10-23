using UnityEditor;
using UnityEngine;

namespace MindGame.EditorTools
{
    /// <summary>
    /// [Camera mouse-look fix, final root cause] In play mode the Input System only
    /// feeds mouse deltas to the game while the GAME VIEW has editor focus. Players
    /// who touch the Scene view / Console (editing assets, checking errors) and hit
    /// Play land with focus elsewhere: WASD keeps working but the chase camera's
    /// yaw/pitch freeze — reported as "camera doesn't follow the mouse".
    /// This tool auto-focuses the Game view when play starts, so mouse-look works
    /// immediately in every session.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayFocus
    {
        static PlayFocus()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode)
                {
                    // Delay one editor tick: focusing during the mode transition is ignored.
                    EditorApplication.delayCall += FocusGameView;
                }
            };
        }

        private static void FocusGameView()
        {
            // "Window/General/Game" focuses (or opens) the Game view — public API.
            if (!EditorApplication.ExecuteMenuItem("Window/General/Game"))
            {
                // Fallback for alternate menu layouts.
                EditorApplication.ExecuteMenuItem("Window/Panels/Game");
            }
        }
    }
}
