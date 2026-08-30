using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class McpMessageHandler
    {
        public static string CurrentActionSource => _currentActionSource;
        private static string _currentActionSource = "human";
        public static void HandleMcpMessage(JObject mcpMessage, TaskCompletionSource<string> completion)
        {
            var action = mcpMessage["method"]?.ToString();
            _currentActionSource = $"agent:{action}";

            var parameters = new List<string>();
            foreach (var property in mcpMessage.Properties())
            {
                // only process "params" key
                if (property.Name is "method" or "jsonrpc" or "id") continue;
                var value = property.Value.ToString();
                if (value.Length > 100)
                    value = value.Substring(0, 97) + "...";
                parameters.Add($"{property.Name}: {value}");
            }

            var paramString = parameters.Count > 0 ? $" ({string.Join(", ", parameters)})" : "";
            BridgeRelay.OnAgentMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {action}{paramString}");

            var resultText = "";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                switch (action)
                {
                    case "get_screenshot":
                        resultText = SceneFunctions.GetScreenshot(mcpMessage);
                        break;

                    case "get_scene_hierarchy":
                        resultText = SceneFunctions.GetSceneHierarchy(mcpMessage);
                        break;

                    case "get_gameobject_tree":
                        resultText = SceneFunctions.GetGameObjectTree(mcpMessage);
                        break;

                    case "notify_unity":
                        var message = mcpMessage["message"]?.ToString();
                        BridgeRelay.OnAgentMessage?.Invoke($"MCP agent: {message}");
                        resultText = "Notification displayed.";
                        break;

                    case "find_unity_files":
                        resultText = AssetFunctions.SearchAssets(mcpMessage);
                        break;

                    case "find_asset_references":
                        resultText = AssetFunctions.FindAssetReferences(mcpMessage);
                        break;

                    case "get_project_tree":
                        resultText = AssetFunctions.GetFolderStructure(mcpMessage);
                        break;

                    case "write_unity_script":
                        resultText = AssetFunctions.WriteScript(mcpMessage);
                        break;

                    case "delete_asset":
                        resultText = AssetFunctions.DeleteAsset(mcpMessage);
                        break;

                    case "get_compilation_status":
                        resultText = AssetFunctions.GetCompilationStatus(mcpMessage);
                        break;

                    case "get_console_logs":
                        resultText = SceneFunctions.GetConsoleLogs();
                        break;

                    case "set_play_mode":
                        var enabled = (bool)mcpMessage["enabled"];
                        resultText = SceneFunctions.SetPlayMode(enabled);
                        break;

                    case "clear_console_logs":
                        resultText = SceneFunctions.ClearConsole();
                        break;

                    case "inspect_gameobject":
                        resultText = SceneFunctions.InspectGameObject(mcpMessage);
                        break;

                    case "get_component_inspector_values":
                    case "Get_InspectorValues":
                        resultText = ComponentFunctions.GetComponentInspectorValues(mcpMessage);
                        break;

                    case "get_component_code":
                    case "Get_ComponentCode":
                        resultText = ComponentFunctions.GetComponentCode(mcpMessage);
                        break;

                    case "get_physics_layers":
                        resultText = SceneFunctions.GetPhysicsMatrix();
                        break;

                    case "add_component":
                        resultText = ComponentFunctions.AddComponent(mcpMessage);
                        break;
                    
                    case "remove_component":
                        resultText = ComponentFunctions.RemoveComponent(mcpMessage);
                        break;

                    case "set_field_values":
                        resultText = ComponentFunctions.SetFieldValues(mcpMessage);
                        break;

                    case "create_gameobject":
                        resultText = HierarchyFunctions.CreateGameObject(mcpMessage);
                        break;

                    case "duplicate_gameobject":
                        resultText = HierarchyFunctions.DuplicateGameObject(mcpMessage);
                        break;

                    case "set_parent":
                        resultText = HierarchyFunctions.SetParent(mcpMessage);
                        break;

                    case "delete_gameobject":
                        resultText = HierarchyFunctions.DeleteGameObject(mcpMessage);
                        break;

                    case "copy_component":
                        resultText = HierarchyFunctions.CopyComponent(mcpMessage);
                        break;

                    case "get_lights_affecting_object":
                        resultText = LightingFunctions.GetLightsAffectingObject(mcpMessage);
                        break;

                    case "get_urp_pipeline_settings":
                        resultText = LightingFunctions.GetUrpPipelineSettings();
                        break;

                    case "get_project_settings":
                        resultText = ProjectSettingsFunctions.GetProjectSettings(mcpMessage);
                        break;

                    case "create_scriptable_object":
                        resultText = ScriptableObjectFunctions.CreateScriptableObject(mcpMessage);
                        break;

                    case "update_scriptable_object":
                        resultText = ScriptableObjectFunctions.UpdateScriptableObject(mcpMessage);
                        break;

                    default:
                        Debug.LogError($"Unhandled MCP command received: {action}");
                        resultText = $"Error: Unhandled action '{action}'.";
                        break;
                }
            }
            catch (BridgeToolException e)
            {
                resultText = e.Message;
            }
            catch (Exception e)
            {
                Debug.LogError($"[MCP] Unhandled exception in '{action}': {e}");
                resultText = $"Error: {e.Message}";
                // LLM can use get_console_logs if it needs e.stacktrace
            }
            finally
            {
                _currentActionSource = "human";
                //Debug.Log($"[MCP] {action} took {sw.ElapsedMilliseconds}ms");
            }

            completion.TrySetResult(resultText);
        }
    }
}
