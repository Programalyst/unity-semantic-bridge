using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// JSON-RPC 2.0 HTTP bridge for Unity Editor.
    /// Inbound (Python -> Unity):
    /// <code>
    /// POST /rpc {"jsonrpc":"2.0","id":"&lt;uuid&gt;","method":"Get_SceneHierarchy","params":{...}}
    /// -> {"jsonrpc":"2.0","id":"&lt;uuid&gt;","result":"&lt;string&gt;"}
    /// or {"jsonrpc":"2.0","id":"&lt;uuid&gt;","error":{"code":-32601,"message":"Method not found"}}
    /// </code>
    /// Batch arrays are supported; every request is acknowledged (notifications
    /// without ``id`` receive ``{"result":"ok","id":null}`` for unified handling).
    /// Outbound (Unity -> Python):
    /// <c>SendNotification("unity/hierarchyChanged", new JObject{...})</c> -> POST http://127.0.0.1:1074/rpc (via <see cref="SendRequestAsync"/>, acked)
    /// <c>SendRequestAsync("unity/ping", ...)</c> -> awaitable JSON-RPC request.
    /// </summary>
    public static class EditorBridge
    {
        private const int Port = 1073;
        private static readonly string Prefix = $"http://127.0.0.1:{Port}/";
        private const string AutoConnectPref = "UnitySemanticBridge_AutoConnect";

        // Python event server (Unity -> Python)
        private const int PythonEventPort = 1074;
        private static readonly string PythonRpcUrl = $"http://127.0.0.1:{PythonEventPort}/rpc";
        private static HttpClient _eventClient;
        private static HttpClient EventClient
        {
            get
            {
                if (_eventClient == null)
                {
                    _eventClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    _eventClient.DefaultRequestHeaders.Add("Accept", "application/json");
                }
                return _eventClient;
            }
        }

        private static HttpListener _listener;
        private static CancellationTokenSource _cts;
        private static Task _acceptLoop;

        public static bool IsConnected => _listener != null && _listener.IsListening;
        public static int ListeningPort => Port;

        private static readonly MainThreadMessageQueue _messageQueue = new();

        // JSON-RPC error codes
        private const int PARSE_ERROR = -32700;
        private const int INVALID_REQUEST = -32600;
        private const int METHOD_NOT_FOUND = -32601;
        private const int INVALID_PARAMS = -32602;
        private const int INTERNAL_ERROR = -32603;

        [InitializeOnLoadMethod]
        private static void OnEditorLoaded()
        {
            _messageQueue.Start(OnMessageReceived);

            BridgeRelay.IsServerConnected = () => IsConnected;

            AssemblyReloadEvents.beforeAssemblyReload -= HandleDomainReloadCleanup;
            AssemblyReloadEvents.beforeAssemblyReload += HandleDomainReloadCleanup;

            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;

            var shouldAutoConnect = EditorPrefs.GetBool(AutoConnectPref, false);
            if (!shouldAutoConnect || IsConnected) return;

            Debug.Log("<color=cyan>[Bridge]</color> Bridge ReInitializing (HTTP JSON-RPC)...");
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

        private static bool _eventHooksInstalled;
        private static double _lastHierarchyEventTime;

        private static void InstallEventHooks()
        {
            if (_eventHooksInstalled) return;
            _eventHooksInstalled = true;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Selection.selectionChanged -= OnSelectionChanged;
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            Application.logMessageReceivedThreaded -= OnLogMessageReceived;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
            Debug.Log("<color=cyan>[Bridge]</color> Unity event hooks installed (hierarchy/selection/playMode/console -> Python).");
        }

        private static void RemoveEventHooks()
        {
            if (!_eventHooksInstalled) return;
            _eventHooksInstalled = false;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            Application.logMessageReceivedThreaded -= OnLogMessageReceived;
            Debug.Log("<color=cyan>[Bridge]</color> Unity event hooks removed.");
        }

        private static void OnHierarchyChanged()
        {
            // Debounce: hierarchyChanged fires very frequently during drags/undos
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastHierarchyEventTime < 0.5) return;
            _lastHierarchyEventTime = now;
            try
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                var payload = new JObject
                {
                    ["scene"] = scene.name,
                    ["timestamp"] = DateTime.UtcNow.ToString("o")
                };
                SendNotification("unity/hierarchyChanged", payload);
            }
            catch (Exception e) { Debug.LogWarning($"[Bridge] OnHierarchyChanged failed: {e.Message}"); }
        }

        private static void OnSelectionChanged()
        {
            try
            {
                var ids = Selection.instanceIDs;
                var payload = new JObject
                {
                    ["instanceIds"] = new JArray(ids),
                    ["count"] = ids.Length
                };
                // Include first selected object's name if available
                if (ids.Length > 0)
                {
                    var obj = EditorUtility.InstanceIDToObject(ids[0]);
                    if (obj != null) payload["firstName"] = obj.name;
                }
                SendNotification("unity/selectionChanged", payload);
            }
            catch (Exception e) { Debug.LogWarning($"[Bridge] OnSelectionChanged failed: {e.Message}"); }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            try
            {
                var payload = new JObject { ["state"] = state.ToString() };
                SendNotification("unity/playModeStateChanged", payload);
            }
            catch (Exception e) { Debug.LogWarning($"[Bridge] OnPlayModeStateChanged failed: {e.Message}"); }
        }

        private static void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            // Only forward warnings and errors to avoid spamming Python with every Debug.Log
            if (type != LogType.Warning && type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            try
            {
                var payload = new JObject
                {
                    ["message"] = message.Length > 1000 ? message.Substring(0, 1000) : message,
                    ["type"] = type.ToString()
                };
                if (!string.IsNullOrEmpty(stackTrace) && stackTrace.Length < 1000)
                    payload["stackTrace"] = stackTrace.Substring(0, Math.Min(1000, stackTrace.Length));
                SendNotification("unity/consoleLog", payload);
            }
            catch { }
        }

        // ---------------------------------------------------------------------
        // Outbound JSON-RPC (Unity -> Python event server) — unified via call_unity style
        // ---------------------------------------------------------------------

        /// <summary>
        /// Send a JSON-RPC 2.0 request to the Python event server and ignore the ack.
        /// Fire-and-forget; safe to call from any thread.
        /// Uses <see cref="SendRequestAsync"/> (with ``id``) so the Python side
        /// acknowledges via ``{"result":"ok"}``.
        /// </summary>
        private static void SendNotification(string method, JObject parms = null)
        {
            _ = SendRequestAsync(method, parms).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var msg = t.Exception?.InnerException?.Message ?? t.Exception?.Message;
                    // Downgrade connect errors to log, others to warning
                    if (msg != null && msg.Contains("Connection"))
                        Debug.Log($"[Bridge] Python event server not reachable for '{method}' (is mcp-editor-bridge running?)");
                    else
                        Debug.LogWarning($"[Bridge] Failed to send event '{method}': {msg}");
                }
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// Send a JSON-RPC 2.0 request (with id) and await the result.
        /// Returns the "result" token or throws on error.
        /// </summary>
        public static async Task<JToken> SendRequestAsync(string method, JObject parms = null, TimeSpan? timeout = null)
        {
            var id = Guid.NewGuid().ToString();
            var payload = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parms ?? new JObject()
            };
            var json = payload.ToString(Formatting.None);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using (var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10)))
            {
                var resp = await EventClient.PostAsync(PythonRpcUrl, content, cts.Token);
                var body = await resp.Content.ReadAsStringAsync();
                var obj = JObject.Parse(body);
                if (obj["error"] != null)
                    throw new InvalidOperationException($"Python RPC error for '{method}': {obj["error"]}");
                return obj["result"];
            }
        }

        // ---------------------------------------------------------------------
        // Inbound HTTP listener (Python -> Unity)
        // ---------------------------------------------------------------------

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

            Debug.Log($"<color=lime>[Bridge]</color> HTTP JSON-RPC listener started on {Prefix} (POST /rpc, GET /health)");
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            InstallEventHooks();
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
            RemoveEventHooks();
        }

        private static void HandleDomainReloadCleanup()
        {
            StopListener();
        }

        private static void OnEditorQuitting()
        {
            EditorPrefs.SetBool(AutoConnectPref, false);
            StopListener();
            try { _eventClient?.Dispose(); } catch { }
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
                    var body = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"protocol\":\"json-rpc-2.0\"}");
                    resp.StatusCode = 200;
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = body.Length;
                    await resp.OutputStream.WriteAsync(body, 0, body.Length);
                    return;
                }

                var path = req.Url.AbsolutePath;
                bool isRpcPath = path == "/rpc" || path == "/jsonrpc";
                if (req.HttpMethod != "POST" || !isRpcPath)
                {
                    resp.StatusCode = 404;
                    var msg = Encoding.UTF8.GetBytes("{\"error\":\"not found. Use POST /rpc (JSON-RPC 2.0) or GET /health\"}");
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
                    // JSON-RPC parse error
                    resp.StatusCode = 400;
                    var err = MakeError(null, PARSE_ERROR, "Parse error: empty body");
                    var bytes = Encoding.UTF8.GetBytes(err.ToString(Formatting.None));
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = bytes.Length;
                    await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    return;
                }

                JToken token;
                try
                {
                    token = JToken.Parse(json);
                }
                catch (Exception e)
                {
                    resp.StatusCode = 400;
                    var err = MakeError(null, PARSE_ERROR, $"Parse error: {e.Message}");
                    var bytes = Encoding.UTF8.GetBytes(err.ToString(Formatting.None));
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = bytes.Length;
                    await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    return;
                }

                // Batch vs single
                if (token is JArray batch)
                {
                    if (batch.Count == 0)
                    {
                        resp.StatusCode = 400;
                        var err = MakeError(null, INVALID_REQUEST, "Invalid Request: empty batch");
                        var bytes = Encoding.UTF8.GetBytes(err.ToString(Formatting.None));
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = bytes.Length;
                        await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        return;
                    }
                    var responses = new JArray();
                    var tasks = new List<Task<JObject>>();
                    foreach (var item in batch)
                    {
                        tasks.Add(HandleSingleJsonRpcAsync(item as JObject));
                    }
                    var results = await Task.WhenAll(tasks);
                    foreach (var r in results) responses.Add(r);
                    var body = Encoding.UTF8.GetBytes(responses.ToString(Formatting.None));
                    resp.StatusCode = 200;
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = body.Length;
                    await resp.OutputStream.WriteAsync(body, 0, body.Length);

                }
                else if (token is JObject obj)
                {
                    var singleResp = await HandleSingleJsonRpcAsync(obj);
                    // Map JSON-RPC error codes to HTTP codes for easier debugging (but still 200 for app errors)
                    int httpCode = 200;
                    if (singleResp["error"] != null)
                    {
                        var code = singleResp["error"]["code"]?.Value<int>() ?? 0;
                        if (code == PARSE_ERROR || code == INVALID_REQUEST) httpCode = 400;
                        else if (code == METHOD_NOT_FOUND) httpCode = 404;
                    }
                    var body = Encoding.UTF8.GetBytes(singleResp.ToString(Formatting.None));
                    resp.StatusCode = httpCode;
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = body.Length;
                    await resp.OutputStream.WriteAsync(body, 0, body.Length);
                }
                else
                {
                    resp.StatusCode = 400;
                    var err = MakeError(null, INVALID_REQUEST, "Invalid Request: expected object or array");
                    var bytes = Encoding.UTF8.GetBytes(err.ToString(Formatting.None));
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = bytes.Length;
                    await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                }
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

        private static JObject MakeError(JToken id, int code, string message, object data = null)
        {
            var err = new JObject { ["code"] = code, ["message"] = message };
            if (data != null) err["data"] = JToken.FromObject(data);
            var resp = new JObject { ["jsonrpc"] = "2.0", ["error"] = err };
            resp["id"] = id ?? JValue.CreateNull();
            return resp;
        }

        private static JObject MakeResult(JToken id, string resultText)
        {
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull(), ["result"] = resultText };
        }

        /// <summary>
        /// Handle a single JSON-RPC object.  Always acknowledged (even notifications without ``id`` get ``id``:null ack).
        /// </summary>
        private static async Task<JObject> HandleSingleJsonRpcAsync(JObject obj)
        {
            if (obj == null)
                return MakeError(null, INVALID_REQUEST, "Invalid Request: not an object");

            // JSON-RPC validation
            var jsonrpc = obj["jsonrpc"]?.ToString();
            if (jsonrpc != "2.0")
                return MakeError(obj["id"], INVALID_REQUEST, "Invalid Request: jsonrpc must be '2.0'");

            var method = obj["method"]?.ToString();
            if (string.IsNullOrEmpty(method))
                return MakeError(obj["id"], INVALID_REQUEST, "Invalid Request: missing 'method'");

            var idToken = obj["id"]; // may be null if notification — still acked with id:null
            var paramsToken = obj["params"];

            // Build a JObject for McpMessageHandler.
            // JSON-RPC params is a named object -> flatten into the handler object.
            JObject messageForHandler;
            if (paramsToken is JObject paramsObj)
            {
                messageForHandler = new JObject { ["method"] = method };
                foreach (var prop in paramsObj.Properties())
                    messageForHandler[prop.Name] = prop.Value;
            }
            else if (paramsToken is JArray paramsArr)
            {
                messageForHandler = new JObject { ["method"] = method, ["params"] = paramsArr };
            }
            else if (paramsToken != null && paramsToken.Type != JTokenType.Null)
            {
                messageForHandler = new JObject { ["method"] = method, ["params"] = paramsToken };
            }
            else
            {
                messageForHandler = new JObject { ["method"] = method };
            }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _messageQueue.Enqueue(messageForHandler.ToString(Formatting.None), tcs);

            // Wait for main-thread handler (60s; screenshot may need longer)
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            if (completed != tcs.Task)
                return MakeError(idToken, INTERNAL_ERROR, "Unity timed out processing the request.");

            var resultText = await tcs.Task;
            return MakeResult(idToken, resultText);
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

            if (message["method"] != null)
            {
                McpMessageHandler.HandleMcpMessage(message, queued.Completion);
            }
            else
            {
                Debug.LogWarning($"[Bridge] Unknown message without method: {queued.Json.Substring(0, Math.Min(200, queued.Json.Length))}");
                queued.Completion.TrySetResult("Error: Unknown message type — expected 'method' field.");
            }
        }
    }
}
