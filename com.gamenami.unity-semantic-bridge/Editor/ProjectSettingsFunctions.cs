using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditorInternal;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class ProjectSettingsFunctions
    {
        private static readonly HashSet<string> AllSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "core", "rendering", "input", "ui", "scripting", "tags_layers", "tagslayers", "tags"
        };

        public static string GetProjectSettings(JObject mcpMessage)
        {
            var sectionsToken = mcpMessage["sections"];
            HashSet<string> requested = null;
            if (sectionsToken is JArray arr && arr.Count > 0)
            {
                requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in arr)
                {
                    var s = t?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(s))
                    {
                        // normalize tags_layers variants
                        if (s.Equals("tagslayers", StringComparison.OrdinalIgnoreCase) || s.Equals("tags", StringComparison.OrdinalIgnoreCase))
                            s = "tags_layers";
                        requested.Add(s.ToLowerInvariant());
                    }
                }
                if (requested.Count == 0) requested = null;
            }
            else if (sectionsToken is JValue v && v.Value != null)
            {
                // allow single string "core" or comma-separated
                var s = v.ToString().Trim();
                if (!string.IsNullOrEmpty(s))
                {
                    requested = new HashSet<string>(s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim().ToLowerInvariant()));
                }
            }

            bool Want(string name) => requested == null || requested.Contains(name.ToLowerInvariant());

            var root = new JObject();

            if (Want("core"))
                root["core"] = GetCore();

            if (Want("rendering"))
                root["rendering"] = GetRendering();

            if (Want("input"))
                root["input"] = GetInput();

            if (Want("ui"))
                root["ui"] = GetUi();

            if (Want("scripting"))
                root["scripting"] = GetScripting();

            if (Want("tags_layers"))
                root["tags_layers"] = GetTagsLayers();

            if (requested != null)
            {
                var unknown = requested.Where(r => !AllSections.Contains(r)).ToArray();
                if (unknown.Length > 0)
                    root["_warning"] = $"Unknown sections: {string.Join(", ", unknown)}. Valid: core, rendering, input, ui, scripting, tags_layers";
            }

            return root.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        private static JObject GetCore()
        {
            var o = new JObject();
            try { o["unityVersion"] = Application.unityVersion; } catch { o["unityVersion"] = "unknown"; }
            try { o["editorPlatform"] = Application.platform.ToString(); } catch { }
            try { o["operatingSystem"] = SystemInfo.operatingSystem; } catch { }
            try { o["companyName"] = PlayerSettings.companyName; } catch (Exception e) { o["companyName"] = $"error: {e.Message}"; }
            try { o["productName"] = PlayerSettings.productName; } catch (Exception e) { o["productName"] = $"error: {e.Message}"; }
            try
            {
                var activeTarget = EditorUserBuildSettings.activeBuildTarget;
                o["activeBuildTarget"] = activeTarget.ToString();
                var group = BuildPipeline.GetBuildTargetGroup(activeTarget);
                o["activeBuildTargetGroup"] = group.ToString();
            }
            catch (Exception e) { o["activeBuildTarget_error"] = e.Message; }
            try { o["bundleVersion"] = PlayerSettings.bundleVersion; } catch { }
            return o;
        }

        private static JObject GetRendering()
        {
            var o = new JObject();
            try
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                if (pipeline == null)
                {
                    o["activePipeline"] = "Built-in";
                    o["pipelineAssetPath"] = null;
                }
                else
                {
                    var typeName = pipeline.GetType().FullName;
                    if (typeName.Contains("Universal")) o["activePipeline"] = "URP";
                    else if (typeName.Contains("HD")) o["activePipeline"] = "HDRP";
                    else o["activePipeline"] = typeName;
                    o["pipelineAssetType"] = typeName;
                    o["pipelineAssetName"] = pipeline.name;
                    var path = AssetDatabase.GetAssetPath(pipeline);
                    o["pipelineAssetPath"] = string.IsNullOrEmpty(path) ? "(embedded)" : path;
                }
            }
            catch (Exception e) { o["pipeline_error"] = e.Message; }

            try { o["colorSpace"] = PlayerSettings.colorSpace.ToString(); } catch (Exception e) { o["colorSpace_error"] = e.Message; }
            try { o["qualityAntiAliasing"] = QualitySettings.antiAliasing; } catch { }
            try { o["activeQualityLevel"] = QualitySettings.names[QualitySettings.GetQualityLevel()]; } catch { }

            // Fold in URP details if URP
            try
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                if (pipeline != null && pipeline.GetType().FullName.Contains("Universal"))
                {
                    // Reuse LightingFunctions logic for details but parse into fields
                    var urpDetails = LightingFunctions.GetUrpPipelineSettings();
                    // urpDetails is a formatted string; keep it as text block for now
                    o["urpDetails"] = urpDetails;
                }
            }
            catch { }

            return o;
        }

        private static JObject GetInput()
        {
            var o = new JObject();
            try
            {
                // PlayerSettings.activeInputHandler: 0=Input Manager (Old), 1=Input System Package (New), 2=Both
                var prop = typeof(PlayerSettings).GetProperty("activeInputHandler", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                {
                    var val = prop.GetValue(null);
                    int intVal = Convert.ToInt32(val);
                    string label = intVal switch
                    {
                        0 => "Input Manager (Old)",
                        1 => "Input System Package (New)",
                        2 => "Both",
                        _ => val?.ToString()
                    };
                    o["activeInputHandler"] = label;
                    o["activeInputHandlerRaw"] = intVal;
                }
                else
                {
                    // Fallback via SerializedObject on ProjectSettings/InputManager.asset is complex; just report unknown
                    o["activeInputHandler"] = "unknown (PlayerSettings.activeInputHandler not found)";
                }
            }
            catch (Exception e) { o["activeInputHandler_error"] = e.Message; }

            try
            {
                // Find default InputActions assets if Input System package is present
                var guids = AssetDatabase.FindAssets("t:InputActionAsset");
                if (guids.Length == 0)
                {
                    // Also try generic search for .inputactions files
                    guids = AssetDatabase.FindAssets("t:DefaultAsset");
                    // Filter manually? Keep empty for now
                    o["inputActionAssets"] = new JArray();
                    o["inputActionAssets_note"] = "No InputActionAsset found (Input System Package may not be installed)";
                }
                else
                {
                    var arr = new JArray();
                    foreach (var guid in guids.Take(10))
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        arr.Add(path);
                    }
                    o["inputActionAssets"] = arr;
                    if (guids.Length > 10) o["inputActionAssets_truncated"] = $"{guids.Length - 10} more";
                }
            }
            catch (Exception e) { o["inputActionAssets_error"] = e.Message; }

            return o;
        }

        private static JObject GetUi()
        {
            var o = new JObject();
            try
            {
                // EventSystem in currently open scenes (uGUI)
                // Use FindObjectsByType if available (2023+), else FindObjectsOfType
                int eventSystemCount = 0;
                try
                {
#if UNITY_2023_1_OR_NEWER
                    eventSystemCount = UnityEngine.Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None).Length;
#else
                    eventSystemCount = UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.EventSystem>().Length;
#endif
                }
                catch { }
                o["eventSystemInOpenScenes"] = eventSystemCount;
                o["eventSystemPresent"] = eventSystemCount > 0;
            }
            catch (Exception e) { o["eventSystem_error"] = e.Message; }

            try
            {
                var uidocGuids = AssetDatabase.FindAssets("t:UIDocument");
                o["uiDocumentAssetCount"] = uidocGuids.Length;
                if (uidocGuids.Length > 0)
                {
                    var arr = new JArray();
                    foreach (var guid in uidocGuids.Take(5))
                        arr.Add(AssetDatabase.GUIDToAssetPath(guid));
                    o["uiDocumentSamplePaths"] = arr;
                }
            }
            catch (Exception e) { o["uiDocument_error"] = e.Message; }

            try
            {
                var vtaGuids = AssetDatabase.FindAssets("t:VisualTreeAsset");
                o["visualTreeAssetCount"] = vtaGuids.Length;
            }
            catch { }
            try
            {
                var styleGuids = AssetDatabase.FindAssets("t:StyleSheet");
                o["uiToolkitStyleSheetCount"] = styleGuids.Length;
            }
            catch { }

            try
            {
                var canvasGuids = AssetDatabase.FindAssets("t:Canvas");
                // Canvas is a component, not an asset type; this will find prefabs containing it via search not ideal
                // Instead count Canvas components in open scenes
                int canvasInScenes = 0;
                try
                {
#if UNITY_2023_1_OR_NEWER
                    canvasInScenes = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None).Length;
#else
                    canvasInScenes = UnityEngine.Object.FindObjectsOfType<Canvas>().Length;
#endif
                }
                catch { }
                o["canvasInOpenScenes"] = canvasInScenes;
            }
            catch { }

            // Simple heuristic signal
            try
            {
                bool hasUGUI = o["eventSystemPresent"]?.Value<bool>() == true || (o["canvasInOpenScenes"]?.Value<int>() ?? 0) > 0;
                bool hasToolkit = (o["uiDocumentAssetCount"]?.Value<int>() ?? 0) > 0 || (o["visualTreeAssetCount"]?.Value<int>() ?? 0) > 0;
                if (hasUGUI && hasToolkit) o["uiSignal"] = "Both uGUI and UI Toolkit in use";
                else if (hasUGUI) o["uiSignal"] = "uGUI (EventSystem/Canvas)";
                else if (hasToolkit) o["uiSignal"] = "UI Toolkit (UIDocument/VisualTreeAsset)";
                else o["uiSignal"] = "No UI system detected in open scenes/project";
            }
            catch { }

            return o;
        }

        private static JObject GetScripting()
        {
            var o = new JObject();
            BuildTargetGroup group = BuildTargetGroup.Unknown;
            try { group = EditorUserBuildSettings.selectedBuildTargetGroup; } catch { }
            try { group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget); } catch { }

            try
            {
                var level = PlayerSettings.GetApiCompatibilityLevel(group);
                o["apiCompatibilityLevel"] = level.ToString();
                o["apiCompatibilityGroup"] = group.ToString();
            }
            catch (Exception e) { o["apiCompatibility_error"] = e.Message; }

            try
            {
                var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
                o["defineSymbolsGroup"] = group.ToString();
                o["defineSymbols"] = defines;
                var split = defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                o["defineSymbolsList"] = new JArray(split);
            }
            catch (Exception e) { o["defineSymbols_error"] = e.Message; }

            try
            {
                // allowUnsafeCode is per-group in newer Unity, but PlayerSettings.allowUnsafeCode is global fallback
                bool allowUnsafe = false;
                var prop = typeof(PlayerSettings).GetProperty("allowUnsafeCode", BindingFlags.Static | BindingFlags.Public);
                if (prop != null)
                    allowUnsafe = (bool)prop.GetValue(null);
                else
                {
                    // Try per-group API via reflection if available
                    var m = typeof(PlayerSettings).GetMethod("GetAllowUnsafeCode", BindingFlags.Static | BindingFlags.Public);
                    if (m != null)
                        allowUnsafe = (bool)m.Invoke(null, new object[] { group });
                }
                o["allowUnsafeCode"] = allowUnsafe;
            }
            catch (Exception e) { o["allowUnsafeCode_error"] = e.Message; }

            try { o["scriptingBackend"] = PlayerSettings.GetScriptingBackend(group).ToString(); } catch { }
            try { o["incrementalGcEnabled"] = PlayerSettings.gcIncremental.ToString(); } catch { }

            return o;
        }

        private static JObject GetTagsLayers()
        {
            var o = new JObject();
            try
            {
                var tags = InternalEditorUtility.tags;
                o["tags"] = new JArray(tags);
                o["tagCount"] = tags.Length;
            }
            catch (Exception e) { o["tags_error"] = e.Message; }

            try
            {
                var layers = InternalEditorUtility.layers;
                o["layers"] = new JArray(layers);
                // Also include index->name map for the 32 slots
                var map = new JObject();
                for (int i = 0; i < 32; i++)
                {
                    var name = LayerMask.LayerToName(i);
                    if (!string.IsNullOrEmpty(name))
                        map[i.ToString()] = name;
                }
                o["layerIndexToName"] = map;
                o["layerCount"] = layers.Length;
            }
            catch (Exception e) { o["layers_error"] = e.Message; }

            return o;
        }
    }
}
