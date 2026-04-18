using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using System.Reflection;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

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
        
        public static string GetComponentCode(JObject mcpMessage) 
        {
            var componentName = mcpMessage["componentName"]?.ToString();
            
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
        
        public static string GetComponentInspectorValues(JObject mcpMessage)
        {
            var id = (int)mcpMessage["instanceID"];
            var compName = mcpMessage["componentName"]?.ToString();
            
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return "Error: GameObject not found.";

            // Find the specific component
            var comp = go.GetComponent(compName);
            if (comp == null) return $"Error: Component '{compName}' not found on {go.name}.";

            var sb = new StringBuilder();
            sb.AppendLine($"--- Inspector: {go.name} > {compName} ---");

            // SerializedObject is the key to seeing what the Editor sees
            SerializedObject so = new SerializedObject(comp);
            SerializedProperty prop = so.GetIterator();

            // Iterate through all visible properties (skips internal Unity fluff)
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false; // Prevents getting stuck in deep sub-properties
                
                // Skip the 'm_Script' field which just points to the C# file
                if (prop.name == "m_Script") continue;

                string valueStr = GetPropertyValue(prop);
                sb.AppendLine($"{prop.displayName} ({prop.name}): {valueStr}");
            }

            return sb.ToString();
        }

        private static string GetPropertyValue(SerializedProperty prop)
        {
            // Handle the most common Unity property types
            return prop.propertyType switch
            {
                SerializedPropertyType.Integer => prop.intValue.ToString(),
                SerializedPropertyType.Boolean => prop.boolValue.ToString(),
                SerializedPropertyType.Float => prop.floatValue.ToString(),
                SerializedPropertyType.String => prop.stringValue,
                SerializedPropertyType.Color => prop.colorValue.ToString(),
                SerializedPropertyType.ObjectReference => prop.objectReferenceValue != null
                    ? prop.objectReferenceValue.name
                    : "None",
                SerializedPropertyType.Enum => prop.enumNames[prop.enumValueIndex],
                SerializedPropertyType.Vector2 => prop.vector2Value.ToString(),
                SerializedPropertyType.Vector3 => prop.vector3Value.ToString(),
                SerializedPropertyType.Rect => prop.rectValue.ToString(),
                SerializedPropertyType.ArraySize => prop.arraySize.ToString(),
                _ => $"[{prop.propertyType}]"
            };
        }
        
        public static string AddComponent(JObject mcpMessage)
        {
            var id = (int)mcpMessage["instanceID"];
            var componentType = mcpMessage["componentType"]?.ToString();
            var allowDuplicate = mcpMessage["allowDuplicate"]?.ToObject<bool>() ?? false;

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return "Error: GameObject not found.";

            // Resolve the type across all loaded assemblies
            Type type = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(componentType);
                if (type != null) break;
            }
            // Fallback: simple name match restricted to Component subclasses
            if (type == null)
            {
                type = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.Name == componentType && typeof(Component).IsAssignableFrom(t));
            }
            if (type == null) return $"Error: Type '{componentType}' not found in any loaded assembly.";

            if (!allowDuplicate)
            {
                var existing = go.GetComponent(type);
                if (existing != null)
                    return $"Skipped: '{componentType}' already exists on '{go.name}' (instanceID: {existing.GetInstanceID()}). Set allowDuplicate=true to override.";
            }

            var added = Undo.AddComponent(go, type);
            if (added == null) return $"Error: AddComponent failed for '{componentType}'. Ensure it is a valid non-abstract Component subclass.";

            EditorUtility.SetDirty(go);

            return $"Added '{type.FullName}' to '{go.name}'. New component instanceID: {added.GetInstanceID()}.";
        }

        public static string SetFieldValue(JObject mcpMessage)
        {
            var id = (int)mcpMessage["instanceID"];
            var componentName = mcpMessage["componentName"]?.ToString();
            var fields = mcpMessage["fields"] as JObject;
            var componentIndex = mcpMessage["componentIndex"]?.ToObject<int>() ?? 0;

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return "Error: GameObject not found.";

            var matches = go.GetComponents<Component>()
                .Where(c => c != null && c.GetType().Name == componentName)
                .ToArray();

            if (matches.Length == 0) return $"Error: Component '{componentName}' not found on '{go.name}'.";
            if (componentIndex >= matches.Length) return $"Error: componentIndex {componentIndex} out of range — found {matches.Length} '{componentName}' component(s).";

            var target = matches[componentIndex];
            var so = new SerializedObject(target);
            Undo.RecordObject(target, $"Set fields on {componentName}");

            var sb = new StringBuilder();
            sb.AppendLine($"--- SetFieldValue: {go.name} > {componentName} ---");

            foreach (var kvp in fields)
            {
                var prop = so.FindProperty(kvp.Key);
                if (prop == null)
                {
                    sb.AppendLine($"{kvp.Key}: ERROR — property not found.");
                    continue;
                }

                try
                {
                    ApplyPropertyValue(prop, kvp.Value);
                    sb.AppendLine($"{kvp.Key}: OK");
                }
                catch (Exception e)
                {
                    sb.AppendLine($"{kvp.Key}: ERROR — {e.Message}");
                }
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(go);

            return sb.ToString();
        }

        private static void ApplyPropertyValue(SerializedProperty prop, JToken value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = value.ToObject<int>(); 
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = value.ToObject<float>(); 
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = value.ToObject<bool>(); 
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = value.ToObject<string>(); 
                    break;
                case SerializedPropertyType.Enum:
                    prop.enumValueIndex = value.Type == JTokenType.String
                        ? Array.IndexOf(prop.enumNames, value.ToObject<string>())
                        : value.ToObject<int>(); 
                    break;
                case SerializedPropertyType.Vector2:
                    var j2 = (JObject)value;
                    prop.vector2Value = new Vector2(j2["x"].ToObject<float>(), j2["y"].ToObject<float>()); 
                    break;
                case SerializedPropertyType.Vector3:
                    var j3 = (JObject)value;
                    prop.vector3Value = new Vector3(j3["x"].ToObject<float>(), j3["y"].ToObject<float>(), j3["z"].ToObject<float>()); 
                    break;
                case SerializedPropertyType.Quaternion:
                    var jq = (JObject)value;
                    prop.quaternionValue = new Quaternion(jq["x"].ToObject<float>(), jq["y"].ToObject<float>(), jq["z"].ToObject<float>(), jq["w"].ToObject<float>()); 
                    break;
                case SerializedPropertyType.Color:
                    var jc = (JObject)value;
                    prop.colorValue = new Color(jc["r"].ToObject<float>(), jc["g"].ToObject<float>(), jc["b"].ToObject<float>(), jc["a"]?.ToObject<float>() ?? 1f); 
                    break;
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = EditorUtility.InstanceIDToObject(value.ToObject<int>()); 
                    break;
                case SerializedPropertyType.LayerMask:
                    prop.intValue = value.ToObject<int>(); 
                    break;
                default:
                    throw new NotSupportedException($"SerializedPropertyType '{prop.propertyType}' is not supported.");
            }
        }
        
        public static string GetLightsAffectingObject(JObject mcpMessage)
        {
            var id = (int)mcpMessage["instanceID"];

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return "Error: GameObject not found.";

            var targetPos = go.transform.position;
            var allLights = Object.FindObjectsOfType<Light>();

            var sb = new StringBuilder();
            sb.AppendLine($"--- Lights Affecting: {go.name} ---");
            sb.AppendLine($"Target Position: {targetPos}");
            sb.AppendLine($"Total lights in scene: {allLights.Length}");
            sb.AppendLine();

            int inRangeCount = 0;

            foreach (var light in allLights)
            {
                float distance = Vector3.Distance(targetPos, light.transform.position);
                bool inRange = light.type == LightType.Directional || distance <= light.range;
                if (inRange) inRangeCount++;

                sb.AppendLine($"Light: {light.name}");
                sb.AppendLine($"  Type:                 {light.type}");
                sb.AppendLine($"  Intensity:            {light.intensity}");
                sb.AppendLine($"  Range:                {(light.type == LightType.Directional ? "N/A (Directional)" : light.range.ToString())}");
                sb.AppendLine($"  Position:             {light.transform.position}");
                sb.AppendLine($"  Lightmapping Mode:    {light.lightmapBakeType}");
                sb.AppendLine($"  Culling Mask:         {light.cullingMask} ({LayerMaskToNames(light.cullingMask)})");
                sb.AppendLine($"  Rendering Layer Mask: {light.renderingLayerMask}");
                sb.AppendLine($"  Distance to Target:   {distance:F3}");
                sb.AppendLine($"  In Range:             {inRange}");
                sb.AppendLine();
            }

            sb.AppendLine($"Lights in range of '{go.name}': {inRangeCount} / {allLights.Length}");

            return sb.ToString();
        }

        private static string LayerMaskToNames(int cullingMask)
        {
            var names = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((cullingMask & (1 << i)) != 0)
                {
                    var layerName = LayerMask.LayerToName(i);
                    if (!string.IsNullOrEmpty(layerName))
                        names.Add(layerName);
                }
            }
            return names.Count > 0 ? string.Join(", ", names) : "None";
        }
        
        public static string GetUrpPipelineSettings()
        {
            var pipelineAsset = GraphicsSettings.currentRenderPipeline;
            if (pipelineAsset == null)
                return "Error: No render pipeline asset assigned in Graphics Settings.";

            var assetType = pipelineAsset.GetType();
            var typeName = assetType.FullName;

            if (typeName != "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset")
                return $"Error: Current pipeline is '{typeName}', not URP.";

            // Helper to safely read a property via reflection
            string Get(string propName)
            {
                var val = assetType.GetProperty(propName)?.GetValue(pipelineAsset);
                return val?.ToString() ?? "N/A";
            }
            
            var sb = new StringBuilder();
            sb.AppendLine("--- URP Pipeline Settings ---");
            sb.AppendLine($"Asset Name:               {pipelineAsset.name}");
            sb.AppendLine($"HDR Enabled:              {Get("supportsHDR")}");
            sb.AppendLine($"MSAA Sample Count:        {Get("msaaSampleCount")}");
            sb.AppendLine($"Shadow Distance:          {Get("shadowDistance")}");
            sb.AppendLine($"Cascade Count:            {Get("shadowCascadeCount")}");
            sb.AppendLine($"Main Light Mode:          {Get("mainLightRenderingMode")}");
            sb.AppendLine($"Additional Lights Mode:   {Get("additionalLightsRenderingMode")}");
            sb.AppendLine($"Per-Object Light Limit:   {Get("maxAdditionalLightsCount")}");
            sb.AppendLine($"Supports Terrain Holes:   {Get("supportsTerrainHoles")}");

            return sb.ToString();
        }
    }
}