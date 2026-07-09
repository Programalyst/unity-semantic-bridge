using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using System.Reflection;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class SceneFunctions
    {
        public static string GetSceneHierarchy(JObject mcpMessage)
        {
            var sceneGenerateConfig = new SceneGenerateSettings
            {
                MaxDepth = mcpMessage["depth"]?.Value<int>() ?? 2,
                MaxNodes = mcpMessage["maxNodes"]?.Value<int>() ?? 300,
                IncludeLayers = mcpMessage["includeLayers"]?.Value<bool>() ?? true,
                IncludeComponents = mcpMessage["includeComponents"]?.Value<bool>() ?? true,
                IncludePositions = mcpMessage["includePositions"]?.Value<bool>() ?? true,
                OnlyMainCamVisible = mcpMessage["onlyMainCamVisible"]?.Value<bool>() ?? true,
                IgnoreDisabled = mcpMessage["ignoreDisabled"]?.Value<bool>() ?? true
            };
            
            var sceneData = SemanticSceneGenerator.Generate(sceneGenerateConfig);
            var sceneJson = JsonConvert.SerializeObject(sceneData, new JsonSerializerSettings
            {
                Formatting = Formatting.None, // Was Formatting.Indented
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
            });
            return sceneJson;
        }

        public static string GetGameObjectTree(JObject mcpMessage)
        {
            var id = (int)mcpMessage["instanceID"];
            var maxDepth = mcpMessage["depth"]?.ToObject<int>() ?? 5;
            var includeComponents = mcpMessage["includeComponents"]?.ToObject<bool>() ?? true;
            var includePositions = mcpMessage["includePositions"]?.ToObject<bool>() ?? false;

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return "Error: GameObject not found.";

            var nodes = new List<object>();
            TraverseTree(go.transform, go.name, 0, maxDepth, includeComponents, includePositions, nodes);

            var result = new
            {
                root = go.name,
                instanceID = id,
                nodeCount = nodes.Count,
                nodes = nodes
            };

            return JsonConvert.SerializeObject(result, Formatting.None, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
        }

        private static void TraverseTree(
            Transform t, string path, int depth, int maxDepth,
            bool includeComponents, bool includePosition,
            List<object> nodes)
        {
            var node = new Dictionary<string, object>
            {
                ["name"]       = t.gameObject.name,
                ["path"]       = path,
                ["instanceId"] = t.gameObject.GetInstanceID(),
            };

            if (includePosition)
                node["position"] = new { x = t.position.x, y = t.position.y, z = t.position.z };

            if (includeComponents)
                node["components"] = t.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToList();

            nodes.Add(node);

            if (depth >= maxDepth) return;

            foreach (Transform child in t)
                TraverseTree(child, $"{path}/{child.name}", depth + 1, maxDepth, includeComponents, includePosition, nodes);
        }
        
        public static string GetConsoleLogs() 
        {
            var sb = new StringBuilder();
        
            // Use reflection to access Unity's internal LogEntries API
            var type = Type.GetType("UnityEditor.LogEntries, UnityEditor");
            if (type == null) return "Couldn't get LogEntries type";
            var getCountMethod = type.GetMethod("GetCount");
            var getEntryMethod = type.GetMethod("GetEntryInternal");
        
            // Create an internal LogEntry object via reflection
            var entryType = Type.GetType("UnityEditor.LogEntry, UnityEditor");
            if (entryType == null) return "Couldn't get LogEntry type";
            var logEntry = Activator.CreateInstance(entryType);

            if (getCountMethod == null) return "Couldn't get GetCount method";
            var count = (int)getCountMethod.Invoke(null, null);
            
            int maxLogs = 10; // Only get the last 10 to save tokens
        
            if (getEntryMethod == null) return "Couldn't get getEntry method";
            for (var i = Math.Max(0, count - maxLogs); i < count; i++) 
            {
                getEntryMethod.Invoke(null, new object[] { i, logEntry });
            
                // Extract fields from the logEntry object
                string message = (string)entryType.GetField("message").GetValue(logEntry);
                // 1. Only take the first line of the message (removes the massive stack trace)
                string firstLine = message.Split('\n')[0];
                
                sb.AppendLine($"{firstLine}");
            }

            return sb.Length > 0 ? sb.ToString() : "Console is empty.";
        }
        
        public static string ClearConsole() {
            var type = Type.GetType("UnityEditor.LogEntries, UnityEditor");
            if (type == null) return "Console cleared.";
            var clearMethod = type.GetMethod("Clear");
            if (clearMethod != null) clearMethod.Invoke(null, null);

            return "Console cleared.";
        }
        
        public static string SetPlayMode(bool enabled) 
        {
            // Must run on main thread
            EditorApplication.delayCall += () => {
                EditorApplication.isPlaying = enabled;
            };
            return $"Initiating Play Mode: {enabled}. Connection will momentarily drop.";
        }
        
        public static string InspectGameObject(JObject mcpMessage) 
        {
            var instanceId = (int)mcpMessage["instanceID"];

            var go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            if (go == null) return "GameObject not found.";

            var sb = new StringBuilder();
            sb.AppendLine($"Name: {go.name} (Layer: {LayerMask.LayerToName(go.layer)})");
    
            foreach (var comp in go.GetComponents<Component>()) 
            {
                if (comp == null) continue;
                sb.AppendLine($"\n[Component: {comp.GetType().Name}]");
                // Use reflection to get public fields (Health, Layer checks, etc.)
                foreach (var field in comp.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) 
                {
                    sb.AppendLine($"  - {field.Name}: {field.GetValue(comp)}");
                }
            }
            return sb.ToString();
        }
        
        public static string GetPhysicsMatrix() 
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- Physics Collision Matrix ---");
            for (int i = 0; i < 32; i++) 
            {
                string layerName = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(layerName)) continue;
        
                for (int j = i; j < 32; j++) 
                {
                    if (Physics.GetIgnoreLayerCollision(i, j)) continue;
                    sb.AppendLine($"{layerName} <--> {LayerMask.LayerToName(j)}: ENABLED");
                }
            }
            return sb.ToString();
        }
        
    }
}