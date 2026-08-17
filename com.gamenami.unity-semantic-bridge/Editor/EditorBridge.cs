using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class EditorBridge
    {
        private const int Port = 1073;
        private static readonly string Prefix = $"http://127.0.0.1:{Port}/";
        private const string AutoConnectPref = "UnitySemanticBridge_AutoConnect";

        private static HttpListener _listener;
        private static CancellationTokenSource _cts;
        private static Task _acceptLoop;

        public static bool IsConnected => _listener != null && _listener.IsListening;
        public static int ListeningPort => Port;

        private static readonly MainThreadMessageQueue _messageQueue = new();

        [InitializeOnLoadMethod]
        private static void OnEditorLoaded()
        {
            _messageQueue.Start(OnMessageReceived);

            BridgeRelay.IsServerConnected = () => IsConnected;

            AssemblyReloadEvents.beforeAssemblyReload -= HandleDomainReloadCleanup;
            AssemblyReloadEvents.beforeAssemblyReload += HandleDomainReloadCleanup;

            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;
            
            // Start tracking undo / redo calls
            CommandFunctions.InitializeCallbacks();

            var shouldAutoConnect = EditorPrefs.GetBool(AutoConnectPref, false);
            if (!shouldAutoConnect || IsConnected) return;

            Debug.Log("<color=cyan>[Bridge]</color> Bridge ReInitializing (HTTP)...");
            EditorApplication.delayCall += () =>
            {
                if (!IsConnected)
                    StartListener();
            };
        }

        public static void ManualConnect()
        {
            EditorPrefs.SetBool(AutoConnectPref, true);
            StartListener();
        }

        public static void ManualDisconnect()
        {
            EditorPrefs.SetBool(AutoConnectPref, false);
            StopListener();
        }

        private static void StartListener()
        {
            if (IsConnected) return;
            StopListener(); // clean any half-open state

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(Prefix);
            try
            {
                _listener.Start();
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[Bridge]</color> HTTP listener failed to start on {Prefix}: {e.Message}");
                StopListener();
                return;
            }

            Debug.Log($"<color=lime>[Bridge]</color> HTTP listener started on {Prefix} (POST /mcp, GET /health)");
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private static void StopListener()
        {
            try { _cts?.Cancel(); } catch { }
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener.Close();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bridge] Error stopping listener: {e.Message}");
            }
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }

        private static void HandleDomainReloadCleanup()
        {
            StopListener();
        }

        private static void OnEditorQuitting()
        {
            EditorPrefs.SetBool(AutoConnectPref, false);
            StopListener();
        }

        private static async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx = null;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    if (!token.IsCancellationRequested)
                        Debug.LogWarning($"[Bridge] Accept error: {e.Message}");
                    break;
                }

                // Fire-and-forget per-request handler so one slow MCP call doesn't block /health
                _ = Task.Run(() => HandleContextAsync(ctx), token);
            }
        }

        private static async Task HandleContextAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            try
            {
                // Health check — no main-thread dispatch needed
                if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/health")
                {
                    var body = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                    resp.StatusCode = 200;
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = body.Length;
                    await resp.OutputStream.WriteAsync(body, 0, body.Length);
                    return;
                }

                if (req.HttpMethod != "POST" || req.Url.AbsolutePath != "/mcp")
                {
                    resp.StatusCode = 404;
                    var msg = Encoding.UTF8.GetBytes("{\"error\":\"not found. Use POST /mcp or GET /health\"}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = msg.Length;
                    await resp.OutputStream.WriteAsync(msg, 0, msg.Length);
                    return;
                }

                string json;
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                    json = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(json))
                {
                    resp.StatusCode = 400;
                    var msg = Encoding.UTF8.GetBytes("{\"error\":\"empty body\"}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = msg.Length;
                    await resp.OutputStream.WriteAsync(msg, 0, msg.Length);
                    return;
                }

                // Dispatch to main thread and await result
                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _messageQueue.Enqueue(json, tcs);

                // Wait for main-thread handler (30s matches Python timeout; screenshot may need longer)
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60)));
                if (completed != tcs.Task)
                {
                    resp.StatusCode = 504;
                    var timeout = Encoding.UTF8.GetBytes("{\"error\":\"Unity timed out processing the request.\"}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = timeout.Length;
                    await resp.OutputStream.WriteAsync(timeout, 0, timeout.Length);
                    return;
                }

                var resultText = await tcs.Task;
                var payload = new JObject { ["content"] = resultText };
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
                resp.StatusCode = 200;
                resp.ContentType = "application/json";
                resp.ContentLength64 = bytes.Length;
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Bridge] HandleContext error: {e}");
                try
                {
                    resp.StatusCode = 500;
                    var err = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { error = e.Message }));
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = err.Length;
                    await resp.OutputStream.WriteAsync(err, 0, err.Length);
                }
                catch { }
            }
            finally
            {
                try { resp.OutputStream.Close(); } catch { }
                resp.Close();
            }
        }

        // Called on main thread via MainThreadMessageQueue.Drain
        private static void OnMessageReceived(MainThreadMessageQueue.QueuedMessage queued)
        {
            JObject message;
            try
            {
                message = JObject.Parse(queued.Json);
            }
            catch (Exception e)
            {
                queued.Completion.TrySetResult($"Error: Invalid JSON: {e.Message}");
                return;
            }

            // Only MCP action messages are expected; gameplay function_call path is removed
            if (message["action"] != null)
            {
                McpMessageHandler.HandleMcpMessage(message, queued.Completion);
            }
            else
            {
                Debug.LogWarning($"[Bridge] Unknown message without action: {queued.Json.Substring(0, Math.Min(200, queued.Json.Length))}");
                queued.Completion.TrySetResult("Error: Unknown message type — expected 'action' field.");
            }
        }
    }
}
