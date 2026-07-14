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
        
        private static readonly MainThreadMessageQueue _messageQueue = new();
        
        // This runs on EVERY domain reload (Play Mode, Scripts, etc.)
        [InitializeOnLoadMethod]
        private static void OnEditorLoaded()
        {
            // Start draining background-thread messages on the main thread via EditorApplication.update
            _messageQueue.Start(OnMessageReceived);
            
            // Tell relay how to check socket state
            BridgeRelay.IsServerConnected = () => IsConnected;
            
            // Clean up / close open sockets BEFORE domain reload
            AssemblyReloadEvents.beforeAssemblyReload -= HandleDomainReloadCleanup;
            AssemblyReloadEvents.beforeAssemblyReload += HandleDomainReloadCleanup;
            
            // Clean up on complete Editor quit
            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;
            
            // only autoConnect if manually connected previously
            var shouldAutoConnect = EditorPrefs.GetBool(AutoConnectPref);
            if (!shouldAutoConnect || IsConnected) return;
            
            Debug.Log("<color=cyan>[Bridge]</color> Bridge ReInitializing...");
            EditorApplication.delayCall += () => 
            {
                if (!IsConnected) 
                    _ = Connect();
            };
        }

        public static void ManualConnect()
        {
            EditorPrefs.SetBool(AutoConnectPref, true);
            _ = Connect();
        }

        private static async Task Connect()
        {
            if (IsConnected) return;
            
            DisconnectNetworkOnly(); // Clean up any old connection attempts

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

        // Runs on background thread due to finally block
        private static void DisconnectNetworkOnly()
        {
            // Cancel first so any in-flight ReceiveAsync exits via OperationCanceledException
            // rather than racing against Dispose() and throwing ObjectDisposedException instead.
            if (_cts != null)
            {
                _cts.Cancel();
            }

            if (_ws != null)
            {
                // Use CancellationToken.None here because we want the close 
                // to attempt to fire even if our main _cts is already cancelled
                if (_ws.State == WebSocketState.Open)
                    _ = _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);

                _ws.Dispose();
                _ws = null;
            }

            _cts?.Dispose();
            _cts = null;
        }

        private static void HandleDomainReloadCleanup()
        {
            // Unity is about to compile, unhook events on the main thread
            BridgeRelay.OnRequestSendToServer -= RuntimeAgentHandler.HandleRequest;
            
            DisconnectNetworkOnly();
        }

        // Cleanup method that handles Unity events on the main thread
        public static void ManualDisconnect()
        {
            EditorPrefs.SetBool(AutoConnectPref, false);
    
            // Unhook Unity events because ManualDisconnect is called from the Main Thread
            BridgeRelay.OnRequestSendToServer -= RuntimeAgentHandler.HandleRequest;
            
            DisconnectNetworkOnly();
        }

        private static async Task ReceiveLoop()
        {
            var buffer = new byte[1024 * 1024]; // 1MB buffer for scene data

            try
            {
                while (IsConnected)
                {
                    using var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    // Loop until we have the FULL message
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    } 
                    while (!result.EndOfMessage);
                    
                    // Now we have the complete string
                    ms.Seek(0, System.IO.SeekOrigin.Begin);
                    using var reader = new System.IO.StreamReader(ms, Encoding.UTF8);
                    var json = await reader.ReadToEndAsync();
                    
                    if (!string.IsNullOrEmpty(json))
                    {
                        _messageQueue.Enqueue(json);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during normal shutdown/domain reload
                // catch so only genuine connection failures are logged
            }
            catch (Exception e)
            {
                if (_ws != null && _ws.State != WebSocketState.Aborted)
                    Debug.LogWarning($"<color=orange>[Bridge]</color> Connection lost: {e.Message}");
            }
            finally
            {
                // Since this runs on a background thread, don't touch EditorApplication directly.
                // Instead, let a fire-and-forget task handle the cleanup safely.
                if (_ws != null)
                {
                    await Task.Run(DisconnectNetworkOnly);
                }
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
        
        public static async Task SendToAgent(object content, string messageType, string requestId = null)
        {
            try
            {
                if (!IsConnected)
                    return;
                
                var response = new JObject
                {
                    ["type"] = messageType,
                    ["content"] = content as string ?? JToken.FromObject(content) // if content is string, do nothing, else convert to nested JSON
                };

                if (requestId != null)
                    response["request_id"] = requestId;
                
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(response));
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
            BridgeRelay.OnRequestSendToServer -= RuntimeAgentHandler.HandleRequest;
            DisconnectNetworkOnly();
        }
    }
}