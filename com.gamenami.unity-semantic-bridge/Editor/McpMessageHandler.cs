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
            // Periodic listener renewal from MCP servers: not an agent action, so it neither
            // tags event sources nor fills the activity log.
            if (action == "register_event_listener")
            {
                completion.TrySetResult(EditorBridge.RegisterEventListener(mcpMessage));
                return;
            }
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
                    case "semantic_batch":
                        resultText = SemanticBatch.Batch(mcpMessage);
                        break;
                    case "acquire_editor_references":
                        resultText = SemanticBatch.AcquireReferences(mcpMessage);
                        break;
                    case "inspect_prefab_assets":
                        resultText = SemanticBatch.InspectPrefabs(mcpMessage);
                        break;

                    case "get_screenshot":
                        resultText = SceneFunctions.GetScreenshot(mcpMessage);
                        break;

                    case "get_scene_hierarchy":
                        resultText = SceneFunctions.GetSceneHierarchy(mcpMessage);
                        break;

                    case "get_gameobject_tree":
                        resultText = SceneFunctions.GetGameObjectTree(mcpMessage);
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

                    case "refresh_assets":
                        resultText = AssetFunctions.RefreshAssets(mcpMessage);
                        break;

                    case "get_compilation_status":
                        resultText = AssetFunctions.GetCompilationStatus(mcpMessage);
                        break;

                    case "get_console_logs":
                        resultText = SceneFunctions.GetConsoleLogs();
                        break;

                    case "open_scene":
                        resultText = PlayModeOrchestrator.OpenScene(mcpMessage);
                        break;

                    case "poll_play_state":
                        resultText = PlayModeOrchestrator.PollPlayState(mcpMessage);
                        break;

                    case "enter_play":
                        // Deferred: ownership of completion passes to the
                        // orchestrator, which resolves it from update ticks.
                        PlayModeOrchestrator.EnterPlay(mcpMessage, completion);
                        return;

                    case "exit_play":
                        PlayModeOrchestrator.ExitPlay(mcpMessage, completion);
                        return;

                    case "invoke_menu_item":
                        resultText = InvocationFunctions.InvokeMenuItem(mcpMessage);
                        break;

                    case "invoke_static":
                        resultText = InvocationFunctions.InvokeStatic(mcpMessage);
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

                    case "set_editor_throttling":
                        resultText = EditorPreferencesFunctions.SetEditorThrottling(mcpMessage);
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

                    case "author_ui":
                        resultText = UiAuthoring.Batch(mcpMessage);
                        break;
                    case "inspect_ui":
                        resultText = UiInspection.Inspect(mcpMessage);
                        break;
                    case "raycast_ui":
                        resultText = UiInspection.Raycast(mcpMessage);
                        break;
                    case "undo_ui_batch":
                        resultText = UiAuthoring.UndoBatch(mcpMessage);
                        break;
                    case "save_ui_context":
                        resultText = UiAuthoring.SaveContext(mcpMessage);
                        break;

                    case "inspect_model_asset":
                        resultText = ModelInspectionFunctions.InspectModelAsset(mcpMessage);
                        break;

                    case "bake_layout":
                        resultText = Layout.LayoutTools.BakeLayout(mcpMessage);
                        break;

                    case "generate_asset_catalog":
                        resultText = AssetCatalogGenerator.GenerateAssetCatalog(mcpMessage);
                        break;

                    case "get_asset_catalog_status":
                        resultText = AssetCatalogGenerator.GetAssetCatalogStatus(mcpMessage);
                        break;

                    case "describe_level_cards":
                        resultText = LevelCatalogGenerator.DescribeLevelCards(mcpMessage);
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
