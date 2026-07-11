using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class McpMessageHandler
    {
        private static volatile bool _isProcessing; // Volatile so all threads see the latest value
        public static async Task HandleMcpMessage(JObject mcpMessage)
        {
            var action = mcpMessage["action"]?.ToString();
            var requestId = mcpMessage["request_id"]?.ToString();
            
            // Guard against MCP server sending concurrent requests
            if (_isProcessing)
            {
                await EditorBridge.SendToAgent("Unity Error: A request is already being processed. Try again shortly.", "mcp_response", requestId);
                return;
            }
            _isProcessing = true;
            
            var parameters = new List<string>();
            foreach (var property in mcpMessage.Properties())
            {
                if (property.Name is "action" or "request_id") continue;
                
                var value = property.Value.ToString();
                // Truncate long content (like script code)
                if (value.Length > 100) 
                    value = value.Substring(0, 97) + "...";
                parameters.Add($"{property.Name}: {value}");
            }
    
            var paramString = parameters.Count > 0 ? $" ({string.Join(", ", parameters)})" : "";
            
            // Display the params in the Activity Log (less "action" and "request_id")
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

                    case "WRITE_SCRIPT":
                        resultText = AssetFunctions.WriteScript(mcpMessage);
                        break;

                    case "GET_CONSOLE_LOGS":
                        resultText = SceneFunctions.GetConsoleLogs();
                        break;

                    case "SET_PLAY_MODE":
                        var enabled = (bool)mcpMessage["enabled"];
                        resultText = SceneFunctions.SetPlayMode(enabled);
                        break;

                    case "CLEAR_CONSOLE_LOGS":
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

                    case "Set_FieldValue":
                        resultText = ComponentFunctions.SetFieldValue(mcpMessage);
                        break;

                    case "Get_LightsAffectingObject":
                        resultText = LightingFunctions.GetLightsAffectingObject(mcpMessage);
                        break;

                    case "Get_UrpPipelineSettings":
                        resultText = LightingFunctions.GetUrpPipelineSettings();
                        break;

                    default:
                        Debug.LogError($"Unhandled MCP command received: {action}");
                        resultText = "Could not handle MCP command";
                        break;
                }
            }
            catch (Exception e)
            {
                // Always send a result 
                resultText = $"Unity Error: {e.Message}\n{e.StackTrace}";
            }
            finally
            {
                Debug.Log($"[MCP] {action} took {sw.ElapsedMilliseconds}ms (request_id={requestId})");
                _isProcessing = false;
            }
            //Debug.Log($"[Result text] {resultText}");
            await EditorBridge.SendToAgent(resultText, "mcp_response", requestId);
        }
    }
}