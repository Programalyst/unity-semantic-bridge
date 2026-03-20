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
    public static class EditorBridge
    {
        private const string ServerUrl = "ws://127.0.0.1:8765";
        private const string AutoConnectPref = "UnitySemanticBridge_AutoConnect";
        
        private static ClientWebSocket _ws;
        private static CancellationTokenSource _cts;

        // Check if the actual websocket is open
        public static bool IsConnected => _ws is { State: WebSocketState.Open };

        // This runs on EVERY domain reload (Play Mode, Scripts, etc.)
        [InitializeOnLoadMethod]
        private static void OnEditorLoaded()
        {
            // only autoConnect if manually connected previously
            var shouldAutoConnect = EditorPrefs.GetBool(AutoConnectPref);
            
            if (!shouldAutoConnect || IsConnected) return;
            
            Debug.Log("<color=cyan>[Bridge]</color> Bridge ReInitializing...");
            EditorApplication.delayCall += () => 
            {
                if (!IsConnected) 
                    _ = Connect();
            };
            
            // set autoConnect to false on Editor quit
            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;
        }

        public static void ManualConnect()
        {
            EditorPrefs.SetBool(AutoConnectPref, true);
            _ = Connect();
        }

        public static void ManualDisconnect()
        {
            EditorPrefs.SetBool(AutoConnectPref, false);
            Disconnect();
        }

        private static async Task Connect()
        {
            if (IsConnected) return;
            
            Disconnect(); // Clean up any old connection attempts

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            try
            {
                await _ws.ConnectAsync(new Uri(ServerUrl), _cts.Token);
                
                _ = ReceiveLoop(); // Start the background listening loop

                // Link existing Runtime Relay events
                BridgeRelay.OnRequestSendToServer -= RuntimeAgentHandler.HandleRequest;
                BridgeRelay.OnRequestSendToServer += RuntimeAgentHandler.HandleRequest;

                Debug.Log($"<color=lime>[Bridge]</color> Connected to USB Agent Server on {ServerUrl}");
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[Bridge]</color> Connection failed: {e.Message}");
            }
        }

        private static void Disconnect()
        {
            BridgeRelay.OnRequestSendToServer -= RuntimeAgentHandler.HandleRequest;

            if (_ws != null)
            {
                // Use CancellationToken.None here because we want the close 
                // to attempt to fire even if our main _cts is already cancelled
                if (_ws.State == WebSocketState.Open)
                    _ = _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        
                _ws.Dispose();
                _ws = null;
            }

            if (_cts == null) return;
            _cts.Cancel(); // This is the "kill switch" for the ReceiveLoop
            _cts.Dispose();
            _cts = null;
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

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Debug.Log("<color=orange>[Bridge]</color> Server initiated close.");
                        break;
                    }
                    
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    
                    // Unity main thread safety
                    EditorApplication.delayCall += () => {
                        OnMessageReceived(json);
                    };
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"<color=orange>[Bridge]</color> Connection lost: {e.Message}");
            }
            finally
            {
                Disconnect();
            }
        }

        private static void OnMessageReceived(string json)
        {
            var message = JsonConvert.DeserializeObject<dynamic>(json);
            if (message.type == "function_call")
            {
                foreach (var call in message.content)
                {
                    RuntimeAgentHandler.HandleFunctionCall(call);
                }
            }
            else if (message.action != null) // all MCP messages have an action field
            {
                //Debug.Log($"[Bridge] Raw JSON {json}");
                McpMessageHandler.HandleMcpMessage(message);
            }
            else 
            {
                Debug.LogWarning($"[Bridge] Unknown response type: {message.type}");
            }
        }
        
        public static async void SendToAgent(object message, string messageType)
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

        private static void OnEditorQuitting()
        {
            EditorPrefs.SetBool(AutoConnectPref, false);
            Disconnect();
        }
    }
}