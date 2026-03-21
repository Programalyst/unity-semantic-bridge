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
    public static class McpFunctions
    {
        public static string SearchAssets(JObject mcpMessage)
        {
            var filter = mcpMessage["filter"]?.ToString();
            var limit = Convert.ToInt32(mcpMessage["limit"]?.ToString());
            var searchInFolders = mcpMessage["folders"]?.ToObject<string[]>() ?? new[] { "Assets" };
            
            var guids = AssetDatabase.FindAssets(filter, searchInFolders);
            var paths = guids.Select(AssetDatabase.GUIDToAssetPath).ToList();
            
            // Limit results to prevent context overflow (mimicking 'head -n 10')
            var resultList = paths.Count > limit ? paths.GetRange(0, limit) : paths;
            var resultText = resultList.Count > 0 
                ? string.Join("\n", resultList) 
                : "No assets found matching that query.";
            return resultText;
        }

        public static string GetSceneHierarchy(JObject mcpMessage)
        {
            var maxDepth = mcpMessage["depth"]?.Value<int>() ?? 2;
            var includeLayers = mcpMessage["includeLayers"]?.Value<bool>() ?? true;
            var includeComponents = mcpMessage["includeComponents"]?.Value<bool>() ?? true;
            var includePositions = mcpMessage["includePositions"]?.Value<bool>() ?? true;

            var sceneGenerateConfig = new SceneGenerateSettings
            {
                MaxDepth = maxDepth,
                IncludeLayers = includeLayers,
                IncludeComponents = includeComponents,
                IncludePositions = includePositions
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

        public static string FindAssetReferences(JObject mcpMessage)
        {
            var assetPath = mcpMessage["path"]?.ToString();
            // Finds everything this asset uses (dependencies)
            string[] deps = AssetDatabase.GetDependencies(assetPath, false);
            var responseContent = deps.Length > 0 ? string.Join("\n", deps) : "No references found.";
            return responseContent;
        }
        
        public static string GetFolderStructure(JObject mcpMessage)
        {
            // 1. Get the path and ensure it's Unity-friendly (forward slashes)
            var folderPath = mcpMessage["path"]?.ToString() ?? "Assets";
            folderPath = folderPath.Replace("\\", "/").TrimEnd('/');

            // 2. Get Sub-folders (using AssetDatabase is much faster)
            string[] subFolders = AssetDatabase.GetSubFolders(folderPath);
    
            // 3. Get Files in this specific folder (depth = false to avoid recursion)
            // We use a filter to ignore .meta files and system files
            string[] assets = AssetDatabase.FindAssets("", new[] { folderPath });
            var filesInFolder = new List<string>();

            foreach (var guid in assets)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                // Only include files DIRECTLY in this folder (not in subfolders)
                if (System.IO.Path.GetDirectoryName(path)?.Replace("\\", "/") == folderPath)
                {
                    filesInFolder.Add(System.IO.Path.GetFileName(path));
                }
            }

            // 4. Format for Claude
            var sb = new StringBuilder();
            sb.AppendLine($"--- Contents of {folderPath} ---");
    
            sb.AppendLine("\n[Directories]:");
            foreach (var dir in subFolders) sb.AppendLine($"  > {System.IO.Path.GetFileName(dir)}/");
    
            sb.AppendLine("\n[Files]:");
            foreach (var file in filesInFolder) sb.AppendLine($"  - {file}");

            return sb.ToString();
        }
        
        public static string WriteScript(JObject mcpMessage)
        {
            var path = mcpMessage["path"]?.ToString();
            var content = mcpMessage["content"]?.ToString();
    
            try 
            {
                // 1. Get absolute path
                if (path != null)
                {
                    var fullPath = System.IO.Path.Combine(Application.dataPath, "..", path);
                    var directory = System.IO.Path.GetDirectoryName(fullPath);

                    // 2. Ensure directory exists (for new scripts)
                    if (directory != null && !System.IO.Directory.Exists(directory))
                        System.IO.Directory.CreateDirectory(directory);

                    // 3. Write the file
                    System.IO.File.WriteAllText(fullPath, content);
                }

                // 4. THE CRITICAL STEP: Tell Unity to refresh
                // This generates .meta files and triggers Domain Reload/Recompilation
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();

                return $"Successfully wrote {path}. Unity is now recompiling...";
            }
            catch (Exception e) 
            {
                return $"Failed to write script: {e.Message}";
            }
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
        
        public static string InspectGameObject(int instanceId) 
        {
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
        
        public static string GetComponentCode(string componentName) 
        {
            // Find the script asset by name
            var guids = AssetDatabase.FindAssets($"{componentName} t:MonoScript");
            if (guids.Length == 0) return $"Source code for {componentName} not found.";

            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            try 
            {
                var fullPath = System.IO.Path.GetFullPath(path);
                return System.IO.File.ReadAllText(fullPath);
            } 
            catch (Exception e) 
            {
                return $"Error reading file: {e.Message}";
            }
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