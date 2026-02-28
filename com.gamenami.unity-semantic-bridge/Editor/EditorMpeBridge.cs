using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.MPE;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class EditorMpeBridge
    {
        private const string ChannelName = "usb-agent-channel";
        private const string PythonHandshakeUrl = "ws://127.0.0.1:8765";
        
        public static bool IsActive => ChannelService.IsRunning() && HasActiveChannel();
        
        private static bool HasActiveChannel()
        {
            // Query Unity's internal MPE list to see if our channel is already there
            var channels = ChannelService.GetChannelList();
            foreach (var channel in channels)
            {
                if (channel.name == ChannelName) return true;
            }
            return false;
        }

        public static void RebindEvents()
        {
            // 1. Re-link the Runtime Agent -> Server path
            BridgeRelay.OnRequestSendToServer -= HandleRuntimeRequest;
            BridgeRelay.OnRequestSendToServer += HandleRuntimeRequest;

            // 2. Re-link the Server -> Unity path
            // Even if the channel is active, the callback might be lost on recompile
            if (IsActive)
            {
                // This ensures the current 'OnMessageReceived' is the one listening
                ChannelService.GetOrCreateChannel(ChannelName, OnMessageReceived);
            }
            Debug.Log("<color=lime>[MPE]</color> Bridge re-synchronized.");
        }
        
        public static async System.Threading.Tasks.Task Connect()
        {
            // If already connected in the background, just re-link the events
            if (IsActive) 
            {
                RebindEvents();
                return;
            }

            // 1. Start Unity's Internal MPE Service
            if (!ChannelService.IsRunning())
            {
                ChannelService.Start();
            }

            // 2. Perform Handshake with Python
            var success = await SendHandshake();

            if (success)
            {
                // 3. Register the incoming message handler
                ChannelService.GetOrCreateChannel(ChannelName, OnMessageReceived);
                
                // 4. Subscribe to the Runtime Relay
                RebindEvents();
                
                Debug.Log($"<color=lime>[MPE]</color> USB Agent Server connected on {ChannelService.GetAddress()}:{ChannelService.GetPort()}");
            }
        }
        
        public static void Disconnect()
        {
            BridgeRelay.OnRequestSendToServer -= HandleRuntimeRequest;
            ChannelService.CloseChannel(ChannelName);
            Debug.Log("<color=orange>[MPE]</color> USB Agent Server disconnected.");
        }
        
        private static async System.Threading.Tasks.Task<bool> SendHandshake()
        {
            using var ws = new ClientWebSocket();
            
            try {
                // Use a short timeout so the UI doesn't hang if the server is off
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.ConnectAsync(new Uri(PythonHandshakeUrl), cts.Token);
                
                int mpePort = ChannelService.GetPort();
                string message = $"{{\"type\": \"mpe_init\", \"port\": {mpePort}}}";
                
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
                return true;
            }
            catch (Exception e) 
            {
                Debug.LogWarning($"[MPE] Handshake failed: {e.Message}. Is the USB Agent server running?");
                return false;
            }
        }

        private static void OnMessageReceived(int clientId, byte[] data)
        {
            string json = Encoding.UTF8.GetString(data);
            var message = JsonConvert.DeserializeObject<dynamic>(json);
            //Debug.Log($"[MPE] Received: {json}");
            if (message.type == "function_call")
            {
                foreach (var call in message.content)
                {
                    HandleFunctionCall(call);
                }
            }
            else if (message.type == "chat_response")
            {
                var text = (string)message.content; // cast to string as "content" is Newtonsoft.Json.Linq.JValue
                
                // Tell window to update from the main thread
                EditorApplication.delayCall += () => {
                    if (SemanticBridgeWindow.Instance)
                        SemanticBridgeWindow.Instance.AddAgentMessage(text);
                };
            }
            else if (message.action != null) // all MCP messages have an action field
            {
                HandleMcpMessage(message);
            }
            else 
            {
                Debug.LogWarning($"[MPE] Unknown response type: {message.type}");
            }
        }

        private static void HandleMcpMessage(JObject contentObj)
        {
            //Debug.Log($"[MPE] Raw JObject {contentObj}");
            var action = contentObj["action"]?.ToString();
            
            switch (action)
            {
                case "MCP_GLOB":
                    var filtered = contentObj["filter"]?.ToString();
                    var limit = Convert.ToInt32(contentObj["limit"]?.ToString());
                    string[] searchFolders = contentObj["folders"]?.ToObject<string[]>() ?? new string[] { "Assets" };
                    var guids = AssetDatabase.FindAssets(filtered, searchFolders);
                    var paths = new List<string>();
                    foreach (var guid in guids) 
                    {
                        paths.Add(AssetDatabase.GUIDToAssetPath(guid));
                    }
                    // Limit results to prevent context overflow (mimicking 'head -n 10')
                    var resultList = paths.Count > limit ? paths.GetRange(0, limit) : paths;
                    var resultText = resultList.Count > 0 
                        ? string.Join("\n", resultList) 
                        : "No assets found matching that query.";

                    // Send back the response
                    SendToAgent(JsonConvert.SerializeObject(new {
                        type = "mcp_response",
                        content = resultText
                    }));
                    break;
                
                case "MCP_NOTIFY":
                    var message = contentObj["message"]?.ToString();
                    Debug.Log($"<color=cyan>[Claude]</color> {message}");
                    SendToAgent(JsonConvert.SerializeObject(new {
                         type = "mcp_response",
                         content = "Notification displayed."
                    }));
                    break;
            }
            
        }

        private static void HandleRuntimeRequest(string json, byte[] image)
        {
            var payload = new {
                sceneJson = JsonConvert.DeserializeObject(json), // Ensures nested JSON is valid
                b64Image = Convert.ToBase64String(image)
            };
            
            SendToAgent(JsonConvert.SerializeObject(payload));
        }

        public static void SendToAgent(string message)
        {
            var channel = ChannelService.GetChannelList();
            foreach (var info in channel)
            {
                if (info.name != ChannelName) continue;
                
                byte[] data = Encoding.UTF8.GetBytes(message);
                ChannelService.Broadcast(info.id, data);
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
            float correctedY = 1f - normalizedY;

            // 2. Convert 0-1 Viewport to Actual Pixels
            var pixelPosition = new Vector2(
                normalizedX * Screen.width,
                correctedY * Screen.height
            );

            Debug.Log($"Viewport: {normalizedX},{normalizedY} -> Pixels: {pixelPosition}");
            
            return pixelPosition;
        }
    }
}