using System;
using UnityEditor;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// Deferral that keeps working while the Editor is in the background. On macOS,
    /// EditorApplication.delayCall callbacks can wait until the Editor is focused again, so
    /// an agent-requested refresh never ran for a hidden Editor. EditorApplication.update
    /// keeps ticking while the Editor is in the background (the bridge's request queue relies on it).
    /// </summary>
    public static class EditorTick
    {
        /// <summary>Runs <paramref name="action"/> once on the next EditorApplication.update.</summary>
        public static void Next(Action action)
        {
            EditorApplication.CallbackFunction once = null;
            once = () =>
            {
                EditorApplication.update -= once;
                action();
            };
            EditorApplication.update += once;
        }
    }
}
