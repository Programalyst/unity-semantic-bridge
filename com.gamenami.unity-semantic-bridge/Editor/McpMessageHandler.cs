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
                    case "Get_Screenshot":
                        resultText = SceneFunctions.GetScreenshot(mcpMessage);
                        break;

                    case "Get_SceneHierarchy":
                        resultText = SceneFunctions.GetSceneHierarchy(mcpMessage);
                        break;

                    case "Get_GameObjectTree":
                        resultText = SceneFunctions.GetGameObjectTree(mcpMessage);
                        break;

                    case "Notify_Unity":
                        var message = mcpMessage["message"]?.ToString();
                        BridgeRelay.OnAgentMessage?.Invoke($"MCP agent: {message}");
                        resultText = "Notification displayed.";
                        break;

                    case "Search_Assets":
                        resultText = AssetFunctions.SearchAssets(mcpMessage);
                        break;

                    case "Find_AssetReferences":
                        resultText = AssetFunctions.FindAssetReferences(mcpMessage);
                        break;

                    case "Get_FolderStructure":
                        resultText = AssetFunctions.GetFolderStructure(mcpMessage);
                        break;

                    case "Write_Script":
                        resultText = AssetFunctions.WriteScript(mcpMessage);
                        break;

                    case "Delete_Asset":
                        resultText = AssetFunctions.DeleteAsset(mcpMessage);
                        break;

                    case "Get_Compilation_Status":
                        resultText = AssetFunctions.GetCompilationStatus(mcpMessage);
                        break;

                    case "Get_Console_Logs":
                        resultText = SceneFunctions.GetConsoleLogs();
                        break;

                    case "Set_Play_Mode":
                        var enabled = (bool)mcpMessage["enabled"];
                        resultText = SceneFunctions.SetPlayMode(enabled);
                        break;

                    case "Clear_Console_Logs":
                        resultText = SceneFunctions.ClearConsole();
                        break;

                    case "Inspect_GameObject":
                        resultText = SceneFunctions.InspectGameObject(mcpMessage);
                        break;

                    case "Get_InspectorValues":
                        resultText = ComponentFunctions.GetComponentInspectorValues(mcpMessage);
                        break;

                    case "Get_ComponentCode":
                        resultText = ComponentFunctions.GetComponentCode(mcpMessage);
                        break;

                    case "Get_PhysicsMatrix":
                        resultText = SceneFunctions.GetPhysicsMatrix();
                        break;

                    case "Add_Component":
                        resultText = ComponentFunctions.AddComponent(mcpMessage);
                        break;
                    
                    case "Remove_Component":
                        resultText = ComponentFunctions.RemoveComponent(mcpMessage);
                        break;

                    case "Set_FieldValues":
                        resultText = ComponentFunctions.SetFieldValues(mcpMessage);
                        break;

                    case "Create_GameObject":
                        resultText = HierarchyFunctions.CreateGameObject(mcpMessage);
                        break;

                    case "Duplicate_GameObject":
                        resultText = HierarchyFunctions.DuplicateGameObject(mcpMessage);
                        break;

                    case "Set_Parent":
                        resultText = HierarchyFunctions.SetParent(mcpMessage);
                        break;

                    case "Delete_GameObject":
                        resultText = HierarchyFunctions.DeleteGameObject(mcpMessage);
                        break;

                    case "Copy_Component":
                        resultText = HierarchyFunctions.CopyComponent(mcpMessage);
                        break;

                    case "Get_LightsAffectingObject":
                        resultText = LightingFunctions.GetLightsAffectingObject(mcpMessage);
                        break;

                    case "Get_UrpPipelineSettings":
                        resultText = LightingFunctions.GetUrpPipelineSettings();
                        break;

                    case "Get_ProjectSettings":
                        resultText = ProjectSettingsFunctions.GetProjectSettings(mcpMessage);
                        break;

                    case "Create_ScriptableObject":
                        resultText = ScriptableObjectFunctions.CreateScriptableObject(mcpMessage);
                        break;

                    case "Update_ScriptableObject":
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
                // LLM can use get_unity_console_logs if it needs e.stacktrace
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
