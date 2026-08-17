using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public class CommandFunctions : MonoBehaviour
    {
        private static bool _isSubscribed = false;
        private static string _lastInterceptedActionName = "None";
        
        // Call this during bridge startup
        public static void InitializeCallbacks()
        {
            if (_isSubscribed) return;
            
            // Subscribe to Unity's native global undo/redo event hook
            Undo.undoRedoEvent += OnGlobalUndoRedoEvent;
            _isSubscribed = true;
        }

        public static string PerformEditorUndo(JObject mcpMessage)
        {
            _lastInterceptedActionName = "None";
        
            // Triggers native Ctrl+Z
            Undo.PerformUndo(); 

            return $"Sent Undo command to Unity. Last state change processed: '{_lastInterceptedActionName}'.";
        }

        public static string PerformEditorRedo(JObject mcpMessage)
        {
            _lastInterceptedActionName = "None";

            // Triggers native Ctrl+Y
            Undo.PerformRedo(); 

            return $"Sent Redo command to Unity. Last state change processed: '{_lastInterceptedActionName}'.";
        }

        /// <summary>
        /// Automatically captures the exact name of the operation that just rolled back/forward
        /// </summary>
        private static void OnGlobalUndoRedoEvent(in UndoRedoInfo info)
        {
            // info.undoName contains the exact string assigned during creation (e.g. "Destroy GameObject")
            _lastInterceptedActionName = string.IsNullOrEmpty(info.undoName) ? "Unnamed Operation" : info.undoName;
        
            // TODO: stream this out as a server-to-client MCP notification event to let Python know the user altered the timeline manually
        }
    }
}