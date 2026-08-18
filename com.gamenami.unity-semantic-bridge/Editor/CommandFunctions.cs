using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public class CommandFunctions
    {
        private static bool _isSubscribed = false;
        private static string _lastInterceptedActionName = "None";
        
        private static readonly object _lock = new();
        private static TaskCompletionSource<string> _pendingUndoRedo;
        
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
            Undo.PerformUndo();
            return "Undo sent. Call get_last_undo_redo_action to see what was actually undone.";
        }

        public static string PerformEditorRedo(JObject mcpMessage)
        {
            Undo.PerformRedo();
            return "Redo sent. Call get_last_undo_redo_action to see what was actually redone.";
        }

        public static string GetLastUndoRedoAction(JObject mcpMessage)
        {
            lock (_lock) { return _lastInterceptedActionName; }
        }
        
        /// <summary>
        /// Automatically captures the exact name of the operation that just rolled back/forward
        /// </summary>
        private static void OnGlobalUndoRedoEvent(in UndoRedoInfo info)
        {
            lock (_lock)
            {
                _lastInterceptedActionName = string.IsNullOrEmpty(info.undoName) ? "Unnamed Operation" : info.undoName;
            }
        }
    }
}