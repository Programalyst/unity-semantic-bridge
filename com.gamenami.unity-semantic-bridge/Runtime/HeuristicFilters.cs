using System;
using System.Linq;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge
{
    public static class HeuristicFilters
    {
        private static readonly string[] _ignoredTypes = {
            "cm", // Cinemachine's internal hidden transform
            "tmp",
            "text",
        };
        
        private static readonly string[] _ignoredCustomTypes = {
            "Manager",
            "Loader",
            "Camera",
            "Target",
            "System",
            "Volume",
            "Cursor",
            "Display",
            "Billboard"
        };

        public static bool IsFolderObject(GameObject obj)
        {
            // There's only 1 component and it's a Transform (RectTransform is also a Transform)
            var components = obj.GetComponents<Component>();
            if (components.Length == 1 && components[0] is Transform) return true;
            
            // UI Folders: If it has UI layout/canvas components
            foreach (var comp in components)
            {
                if (comp is Canvas or UnityEngine.UI.LayoutGroup) return true;
            }
            
            return false;
        }

        public static bool IsGameplayObject(GameObject obj) 
        {
            // Exclude UI text and Cinemachine internals
            if (_ignoredTypes.Any(t => obj.name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return false;
            
            // Exclude "Managers" and other custom systems
            if (_ignoredCustomTypes.Any(t => obj.name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return false;
            
            foreach (var comp in obj.GetComponents<Component>()) 
            {
                // Use strings for names to avoid assembly dependency errors
                var type = comp.GetType();
                var shortName = type.Name; // SHORT NAME for specific "System" exclusions
                var fullName = type.FullName ?? ""; // FULL NAME for "Game Logic" detection
                
                // If it has custom script components then it is gameplay related
                if (!fullName.StartsWith("UnityEngine.") && !fullName.StartsWith("UnityEditor.")) 
                    return true;
                
                // Interaction components = ALWAYS include
                if (comp is Collider or UnityEngine.UI.Button)
                    return true;
            }
            
            return false;
        }

        public static bool IsFunctionalComponent(Component comp)
        {
            var type = comp.GetType();
            var fullName = type.FullName ?? ""; // FULL NAME for "Game Logic" detection
    
            // 1. Skip the standard "Engine Noise" to save tokens
            if (comp is Transform || comp is Animator || comp is Rigidbody || fullName.Contains("Animation")) 
                return false;
            
            // Skip objects that only exist for Rendering/UI/Audio
            if (_ignoredTypes.Any(t => fullName.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return false;
            
            // 2. Always include custom scripts (not starting with UnityEngine/UnityEditor)
            if (!fullName.StartsWith("UnityEngine.") && !fullName.StartsWith("UnityEditor.")) 
                return true;
    
            // 3. Keep essential physics/interaction components
            return comp is Collider or UnityEngine.UI.Button;
        }
    }
}
