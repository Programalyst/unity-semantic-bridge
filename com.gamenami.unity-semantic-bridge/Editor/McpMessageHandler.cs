using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class McpMessageHandler
    {
        public static void HandleMcpMessage(JObject mcpMessage)
        {
            var action = mcpMessage["action"]?.ToString();
            var parameters = new List<string>();
            foreach (var property in mcpMessage.Properties())
            {
                if (property.Name == "action") continue;
                
                var value = property.Value.ToString();
                // Truncate long content (like script code)
                if (value.Length > 100) 
                    value = value.Substring(0, 97) + "...";
                parameters.Add($"{property.Name}: {value}");
            }
    
            var paramString = parameters.Count > 0 ? $" ({string.Join(", ", parameters)})" : "";
            BridgeRelay.OnAgentMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {action}{paramString}");

            var resultText = "";
            try
            {
                switch (action)
                {
                    case "Get_SceneHierarchy":
                        resultText = McpFunctions.GetSceneHierarchy(mcpMessage);
                        break;

                    case "Notify_Unity":
                        var message = mcpMessage["message"]?.ToString();
                        BridgeRelay.OnAgentMessage?.Invoke($"MCP agent: {message}");
                        resultText = "Notification displayed.";
                        break;

                    case "Search_Assets":
                        resultText = McpFunctions.SearchAssets(mcpMessage);
                        break;

                    case "Find_AssetReferences":
                        resultText = McpFunctions.FindAssetReferences(mcpMessage);
                        break;

                    case "Get_FolderStructure":
                        resultText = McpFunctions.GetFolderStructure(mcpMessage);
                        break;

                    case "WRITE_SCRIPT":
                        resultText = McpFunctions.WriteScript(mcpMessage);
                        break;

                    case "GET_CONSOLE_LOGS":
                        resultText = McpFunctions.GetConsoleLogs();
                        break;

                    case "SET_PLAY_MODE":
                        var enabled = (bool)mcpMessage["enabled"];
                        resultText = McpFunctions.SetPlayMode(enabled);
                        break;

                    case "CLEAR_CONSOLE_LOGS":
                        resultText = McpFunctions.ClearConsole();
                        break;

                    case "Inspect_GameObject":
                        resultText = McpFunctions.InspectGameObject(mcpMessage);
                        break;

                    case "Get_InspectorValues":
                        resultText = McpFunctions.GetComponentInspectorValues(mcpMessage);
                        break;

                    case "Get_ComponentCode":
                        resultText = McpFunctions.GetComponentCode(mcpMessage);
                        break;

                    case "Get_PhysicsMatrix":
                        resultText = McpFunctions.GetPhysicsMatrix();
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
            //Debug.Log($"[Result text] {resultText}");
            EditorBridge.SendToAgent(resultText, "mcp_response");
        }
    }
}