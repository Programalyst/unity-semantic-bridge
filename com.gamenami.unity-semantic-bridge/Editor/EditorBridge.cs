using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace Gamenami.UnitySemanticBridge.Editor
{
    [InitializeOnLoad] // Makes constructor run every time Unity finishes compiling
    public static class EditorBridge
    {
        private const string ServerUrl = "ws://127.0.0.1:8765";
        private static ClientWebSocket _ws;
        private static CancellationTokenSource _cts;

        // Check if the actual websocket is open
        public static bool IsConnected => _ws is { State: WebSocketState.Open };
        
        static EditorBridge() 
        {
            Debug.Log("[Bridge] Recompile detected, checking connection...");
            EditorApplication.delayCall += () => { _ = Connect(); };
        }

        public static async Task Connect()
        {
            if (IsConnected) return;

            // Clean up any old connection attempts
            await Disconnect();

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            try
            {
                Debug.Log($"<color=cyan>[Bridge]</color> Connecting to {ServerUrl}...");
                await _ws.ConnectAsync(new Uri(ServerUrl), _cts.Token);
                
                // Start the background listening loop
                _ = ReceiveLoop();

                // Link your existing Runtime Relay events
                BridgeRelay.OnRequestSendToServer -= HandleRuntimeRequest;
                BridgeRelay.OnRequestSendToServer += HandleRuntimeRequest;

                Debug.Log("<color=lime>[Bridge]</color> Connected to USB Agent Server.");
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[Bridge]</color> Connection failed: {e.Message}");
            }
        }

        public static async Task Disconnect()
        {
            BridgeRelay.OnRequestSendToServer -= HandleRuntimeRequest;

            if (_ws != null)
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                _ws.Dispose();
                _ws = null;
            }

            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }
        
        [InitializeOnLoadMethod] // ensures it stays wired up
        private static void SetupRelay()
        {
            // Tell relay how to check socket state
            BridgeRelay.IsServerConnected = () => IsConnected;
        }

        private static async Task ReceiveLoop()
        {
            var buffer = new byte[1024 * 1024]; // 1MB buffer for scene data

            try
            {
                while (IsConnected)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    
                    // Unity main thread safety
                    EditorApplication.delayCall += () => {
                        OnMessageReceived(json);
                    };
                }
            }
            catch (Exception e)
            {
                if (!_cts.IsCancellationRequested)
                    Debug.LogWarning($"[Bridge] Connection lost: {e.Message}");
            }
            finally
            {
                await Disconnect();
            }
        }

        private static void OnMessageReceived(string json)
        {
            var message = JsonConvert.DeserializeObject<dynamic>(json);
            if (message.type == "function_call")
            {
                foreach (var call in message.content)
                {
                    HandleFunctionCall(call);
                }
            }
            else if (message.action != null) // all MCP messages have an action field
            {
                HandleMcpMessage(message);
            }
            else 
            {
                Debug.LogWarning($"[Bridge] Unknown response type: {message.type}");
            }
        }

        private static void HandleMcpMessage(JObject mcpMessage)
        {
            Debug.Log($"[MPE] Raw JObject {mcpMessage}");
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
            }
            Debug.Log($"[Result text] {resultText}");
            SendToAgent(resultText, "mcp_response");
        }

        private static void HandleRuntimeRequest(List<string> agentActions, SemanticScene sceneData, byte[] image)
        {
            var sceneJson = JsonConvert.SerializeObject(sceneData, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            });
            
            var payload = new {
                agentActions,
                sceneJson,
                b64Image = Convert.ToBase64String(image)
            };
            Debug.Log($"[Bridge] Context sent. Scene JSON size: {sceneJson.Length / 1024}KB. Image size: {image.Length / 1024}KB.");
            SendToAgent(payload, "gameplay_response");
        }
        
        private static async void SendToAgent(object message, string messageType)
        {
            try
            {
                if (!IsConnected) return;
            
                var json = JsonConvert.SerializeObject(new
                {
                    type = messageType,
                    content = message
                });
                
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);

            }
            catch (Exception e)
            {
                Debug.LogError($"[Bridge] Send failed: {e.Message}");
            }
        }
        
        private static void HandleFunctionCall(dynamic call)
        {
            string funcName = call.name;
            var args = call.args;
            var intent = call.args.Intent != null ? (string)call.args.Intent : "No Intent";

            // Wrapping in delayCall ensures the click happens safely on the main thread during the next editor update
            EditorApplication.delayCall += () =>
            {
                switch (funcName)
                {
                    case "click_screen_position":
                    {
                        // Gemini sends 0-1 Viewport coordinates
                        var vx = (float)args.screenX;
                        var vy = (float)args.screenY;
                    
                        AgentCommandRelay.ExecuteScreenClick(ConvertToScreenPosition(vx, vy));
                        break;
                    }
                    case "click_ui_button":
                        AgentCommandRelay.ExecuteButtonClick(args.ButtonName.ToString());
                        break;
                }
                AgentCommandRelay.CommandReceived(intent); // allow GameplayAgent to act again
            };
        }
        
        private static Vector2 ConvertToScreenPosition(float normalizedX, float normalizedY)
        {
            // 1. Flip Y back (Unity Screen/Viewport Y is bottom-up, LLM Y is top-down)
            var correctedY = 1f - normalizedY;

            // 2. Convert 0-1 Viewport to Actual Pixels
            var pixelPosition = new Vector2(
                normalizedX * Screen.width,
                correctedY * Screen.height
            );

            //Debug.Log($"Viewport: {normalizedX},{normalizedY} -> Pixels: {pixelPosition}");
            
            return pixelPosition;
        }
    }
}