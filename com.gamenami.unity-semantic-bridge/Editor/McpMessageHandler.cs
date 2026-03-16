using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class McpMessageHandler
    {
        public static void HandleMcpMessage(JObject mcpMessage)
        {
            var action = mcpMessage["action"]?.ToString();

            var resultText = "";
            switch (action)
            {
                case "MCP_GLOB":
                    resultText = McpFunctions.SearchAssets(mcpMessage);
                    break;
                
                case "MCP_NOTIFY":
                    var message = mcpMessage["message"]?.ToString();
                    Debug.Log($"<color=cyan>[USB Agent]</color> {message}");
                    resultText ="Notification displayed.";
                    break;
                
                case "MCP_GET_SCENE":
                    resultText = McpFunctions.GetSemanticScene(mcpMessage);
                    break;
                
                case "MCP_GREP":
                    resultText = McpFunctions.FindAssetReferences(mcpMessage);
                    break;
                
                case "MCP_TREE":
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
            }
            Debug.Log($"[Result text] {resultText}");
            EditorBridge.SendToAgent(resultText, "mcp_response");
        }
    }
}