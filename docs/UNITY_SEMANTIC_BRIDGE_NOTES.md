# Unity Semantic Bridge — Repo Notes

Exploration date: 2026-08-09 | Unity 2022.3 LTS | Package `com.gamenami.unity-semantic-bridge` + Python MCP Server (`Server/`)

## 1. What this is
MCP bridge over a localhost WebSocket (`ws://127.0.0.1:8765`) that lets an IDE agent (Claude Code / Cursor / Antigravity etc.) drive the Unity Editor and optionally run a vision-driven Gameplay Agent in Play Mode. The Server is launched via MCP stdio (`uv --directory Server run main.py`); Unity's `EditorBridge` connects out to it. MCP tools fan out as JSON `action` messages → Unity Editor executes them on the main thread → `mcp_response` (correlated by `request_id`) returns.

```
IDE Agent --(stdio MCP)--> FastMCP Server (Server/main.py) --(WS :8765)--> EditorBridge.cs --(MainThreadMessageQueue)--> McpMessageHandler / RuntimeAgentHandler
                              ^---- LightingAgent (LangGraph + injected LLM) / Gameplay image_analysis (injected LLM) also live in Server
```

## 2. Server (`Server/` — Python >=3.13, `uv`, `fastmcp>=2.12.5`, `websockets<15`)

- **`main.py`** — `FastMCP("UnitySceneSubAgent")` + `websockets.serve(handle_unity_connection)` on `127.0.0.1:8765`. `handle_unity_connection` stores `app_state.unity_ws` and forwards every incoming string to `unity_bridge.handle_unity_message`. `mcp.run_stdio_async()` controls lifecycle (when Claude closes the pipe the `async with websockets.serve` exits).
- **`state_manager.py`** — `AppState` dataclass: `unity_ws`, `pending_requests: dict[request_id, Future]`, `unity_request_lock` (serializes MCP→Unity). `base_dir` resolves relative to `state_manager.py` for `system_prompt.txt` etc.
- **`unity_bridge.py`**
  - `forward_to_unity(payload)` — injects `request_id=uuid4`, registers a `Future` in `pending_requests`, holds `unity_request_lock`, `ws.send(json)`, `wait_for(..., 60s)` → string result. “not connected” fast path.
  - `fetch_screenshot_base64()` — same pattern for `Get_Screenshot` but extracts `result["content"]` (base64 JPEG). Raises if disconnected.
  - `handle_unity_message(raw)` — dispatch on `type`: `mcp_response` → fulfills matching Future; `gameplay_response` → guarded by `processing_lock` + vision gate (drop if locked/text-only), unpacks `agentActions/sceneJson/b64Image`, calls `Runtime.image_analysis.analyze_gameplay_scene(..., config)` (injected LLM) → `AIMessage.tool_calls` → sends `{"type":"function_call","content":[...]}`
- **`mcp_tools.py:register_unity_tools(mcp)`** — ~19 tools, all thin wrappers over `forward_to_unity` (except `diagnose_lighting_issue` & `get_screenshot`):
  - Scene: `get_scene_hierarchy`, `get_gameobject_tree`, `inspect_gameobject`, `get_component_inspector_values`, `get_component_code`, `add_component`, `set_field_value`
  - Lighting: `get_lights_affecting_object`, `get_urp_pipeline_settings`, `diagnose_lighting_issue`
  - Assets: `find_unity_files` (→ `Search_Assets`), `find_asset_references`, `get_project_tree` (`Get_FolderStructure`), `write_unity_script` (`WRITE_SCRIPT`)
  - Editor/runtime: `get_screenshot`, `set_unity_play_mode`, `get_unity_console_logs`/`clear_unity_console_logs`, `get_unity_physics_layers`, `notify_unity`
  - Note `get_screenshot` returns `mcp.server.fastmcp.Image` (base64→bytes conversion in tool).
- **`Runtime/image_analysis.py`** — Gameplay vision agent. Now uses injected `RunnableConfig` LLM (`analyze_gameplay_scene` with `@tool click_screen_position`/`click_ui_button`, vision-gated, no `genai.Client`). Loads `system_prompt.txt`, builds `HumanMessage(image_url=data:...)` + JSON context, `llm.bind_tools(GAMEPLAY_TOOLS).ainvoke(...)` → `AIMessage.tool_calls`.
- **`Runtime/system_prompt.txt`** — Ares tactician prompt (turn-based game). Movement/attack phrasing, viewportPos semantics, unit selection at feet, etc.
- **`LightingAgent/lighting_agent.py`** — LangGraph `StateGraph(AgentState)` with nodes `diagnose → tools → check_resolution`. Uses injected `RunnableConfig` LLM (`_get_llm(config)`). `search_unity_docs` uses the same injected LLM. `tools_condition` routes diagnose→tools vs diagnose→check_resolution; early abort after 2 consecutive tool errors or `max_iterations`. Entry `diagnose_lighting_issue(instance_id, issue_description, config)` captures screenshot via `fetch_screenshot_base64` for visual context.
- Env: No separate API key required — subagents/gameplay reuse injected `RunnableConfig` LLM via vision gate. `pyproject.toml` core deps: `dotenv`, `fastmcp`, `websockets`, `langgraph`, `langchain-core`.

## 3. Unity Package (`com.gamenami.unity-semantic-bridge` — `unity:2022.3`, dep `com.unity.nuget.newtonsoft-json:3.2.2`)

### 3a. Editor (`Editor/`)
- **`EditorBridge.cs`** (static) — `ClientWebSocket` to `ws://127.0.0.1:8765`, 1 MB buffer. `InitializeOnLoadMethod OnEditorLoaded` wires `MainThreadMessageQueue`, `BridgeRelay.IsServerConnected`, domain-reload (`AssemblyReloadEvents.beforeAssemblyReload`) and quit cleanup; auto-connect if `EditorPrefs[AutoConnectPref]=true`. `Connect()` → `ReceiveLoop()` (background, reassembles fragmented messages via `MemoryStream` until `EndOfMessage`). `DisconnectNetworkOnly()` cancels CTS first to avoid `ObjectDisposedException`. `OnMessageReceived(json)` on main thread: `type=="function_call"` → `RuntimeAgentHandler.HandleFunctionCall` per call; `action!=null` → `McpMessageHandler.HandleMcpMessage`; else warning. `SendToAgent(content, messageType, requestId)` wraps `{"type","content","request_id"}` via `JObject`.
- **`MainThreadMessageQueue.cs`** — marshals background WS thread → `EditorApplication.update` so Unity API runs on main thread.
- **`McpMessageHandler.cs`** (static) — single-flight guard `_isProcessing` (volatile): concurrent MCP requests get immediate `"Unity Error: A request is already being processed."` Logs `BridgeRelay.OnAgentMessage` with timestamp + truncated params. Switch on `action`: `Get_Screenshot`→`SceneFunctions.GetScreenshot`, `Get_SceneHierarchy`→`SceneFunctions.GetSceneHierarchy`, `Get_GameObjectTree`, `Notify_Unity`, `Search_Assets`/`Find_AssetReferences`/`Get_FolderStructure`/`WRITE_SCRIPT` → `AssetFunctions`, `GET_CONSOLE_LOGS`/`SET_PLAY_MODE`/`CLEAR_CONSOLE_LOGS`/`Inspect_GameObject`/`Get_PhysicsMatrix` → `SceneFunctions`, `Get_InspectorValues`/`Get_ComponentCode`/`Add_Component`/`Set_FieldValue` → `ComponentFunctions`, `Get_LightsAffectingObject`/`Get_UrpPipelineSettings` → `LightingFunctions`. All wrapped with `Stopwatch` log; `finally` resets `_isProcessing` and `await EditorBridge.SendToAgent(resultText, "mcp_response", requestId)`.
- **`SceneFunctions.cs`** — `GetScreenshot(jpgQuality=50, maxWidth=1280)` → Edit Mode only (Play Mode returns error), via `EditModeScreenshotTool.CaptureSceneViewJpg` → base64. `GetSceneHierarchy` → builds `SceneGenerateSettings` from message (`depth=2, maxNodes=300` defaults) → `SemanticSceneGenerator.Generate(settings)` → `JsonConvert.SerializeObject(Formatting.None, NullValueHandling.Ignore)`. `GetGameObjectTree` → `EditorUtility.InstanceIDToObject(id)` → recursive `TraverseTree` (nodes: name/path/instanceId/[position]/components). `GetConsoleLogs` (reflection `UnityEditor.LogEntries`+`LogEntry`, last 10, first line only), `ClearConsole`, `SetPlayMode` (deferred `EditorApplication.delayCall`), `InspectGameObject` (reflection public fields), `GetPhysicsMatrix` (32-layer collision matrix).
- **`ComponentFunctions.cs`** — `GetComponentCode(componentName)` → `AssetDatabase.FindAssets("... t:MonoScript")` → `File.ReadAllText`. `GetComponentInspectorValues` → `SerializedObject` iterator (`NextVisible`, skip `m_Script`, `GetPropertyValue` switch on `SerializedPropertyType`). `AddComponent(instanceID, componentType, allowDuplicate)` → resolve `Type` across assemblies (full name then short-name `Component` subclass), `Undo.AddComponent`, `SetDirty`. `SetFieldValue` → `SerializedObject.FindProperty(kvp.Key)` + `ApplyPropertyValue` (int/float/bool/string/enum/Vector2/3/Quaternion/Color/ObjectReference/layerMask), `Undo.RecordObject`, `ApplyModifiedProperties`.
- **`AssetFunctions.cs`** — `SearchAssets(filter, limit, searchInFolders)` → `AssetDatabase.FindAssets`. `FindAssetReferences` → `AssetDatabase.GetDependencies`. `GetFolderStructure` → `AssetDatabase.GetSubFolders` + filtered `FindAssets`. `WriteScript(path, content)` → `File.WriteAllText` + `ImportAsset(ForceUpdate)` + `Refresh` (recompile).
- **`LightingFunctions.cs`** — `GetLightsAffectingObject(instanceID)` — picks `Renderer.bounds` or `Collider.bounds` (fallback to pivot), `FindObjectsOfType<Light>()`, per-light distance to `bounds.ClosestPoint` (so terrain/large planes not misjudged by pivot), `inRange = Directional || distance<=range`, reports culling/ renderingLayerMask, totals `inRangeCount`. `GetUrpPipelineSettings()` — reflection on `UniversalRenderPipelineAsset` (`m_RendererDataList`/`m_DefaultRendererIndex` → `renderingModeRequested`/`m_RenderingMode`) + generic property dump; returns errors if pipeline is null or not URP.
- **`EditModeScreenshotTool.cs`**, **`RuntimeAgentHandler.cs`** — screenshot capture in Edit Mode vs Play Mode agent forwarding; `RuntimeAgentHandler` wires `BridgeRelay.OnRequestSendToServer` to forward GameplayAgent payloads.
- **`Window/SemanticBridgeWindow.cs`** (`Tools/Unity Semantic Bridge`) — `EditorWindow` with tabs Editor Mode / Gameplay Mode, connection header (● Connected/○ Offline + Connect/Disconnect), `BridgeRelay.OnAgentMessage` log scroll (`_agentHistory`), PlayMode `SemanticSceneConfigSo` selector, agent start/stop (delegates to `GameplayAgent`).

### 3b. Runtime (`Runtime/`)
- **`SemanticScene.cs` / `SemanticSceneGenerator.cs`** — `SemanticScene {sceneName, sceneContext, nodes: List<SemanticNode>, truncated, totalNodesVisited, LayerCounts}`; `SemanticNode {name, path, instanceId, layer, position/rotation/scale, viewportPos (0-1, y flipped), components}` with custom `SimpleVec3`/`SimpleVec2` (rounded, avoid `Vector3` serialization loops). Generator has two overloads:
  - Editor: `Generate(SceneGenerateSettings{MaxDepth, MaxNodes, IncludeLayers/Components/Positions, OnlyMainCamVisible, IgnoreDisabled})` — BFS over `SceneManager.GetActiveScene().GetRootGameObjects()`, prunes SkinnedMeshRenderer subtrees, optional viewport culling, respects `MaxNodes` with `truncated` note.
  - Gameplay: `Generate(SemanticSceneConfigSo)` — uses `HeuristicFilters`, viewport culling, `excludeLayers` mask, depth handling where `IsFolderObject` doesn't increment depth; collects viewportPos for every node, populates transforms/layers conditionally.
- **`HeuristicFilters.cs`** — `IsFolderObject` (only Transform / Canvas/LayoutGroup) → folder depth trick; `IsGameplayObject` (name contains ignored tokens → skip `cm/tmp/text` + `Manager/Loader/Camera/...`; custom script or Collider/Button → include); `IsFunctionalComponent` (skip Transform/Animator/Rigidbody/Animation, rendering noise, include custom scripts + Collider/Button).
- **`GameplayAgent.cs`** (`AgentSingleton<GameplayAgent>`) — `StartAgentLoop/StopAgentLoop`, `AGENT_INTERVAL=3s`, `agentActions: List<string>`, `_awaitingResponse` gating. `Update` → if running/connected/canAct/not processing and cooldown elapsed → `CaptureAndSend()` → `Generate(configAsset)` + `ScreenshotTool.GetScreenshotBytes(cb)` → `BridgeRelay.Send(actions, sceneData, bytes)`. `HandleActionComplete(intent)` (via `AgentCommandRelay.OnCommandReceived`) appends `Step N: intent` and clears `_awaitingResponse`; broadcasts to `BridgeRelay.OnAgentMessage`.
- **`Relays/`** — `BridgeRelay` (static event bus: `OnRequestSendToServer(List<string>, SemanticScene, byte[])`, `IsServerConnected Func<bool>`, `OnAgentMessage Action<string>`), `AgentStateRelay`/`AgentCommandRelay`, `BridgeRelay.Send` helper.
- **`Tools/ScreenshotTool.cs`**, **`CanvasButtonFinder.cs`**, **`Utils/HiddenSingleton.cs`+`AgentSingleton.cs`** — runtime screenshot & UI lookup singletons.
- **`Settings/SemanticSceneConfigSo.cs` + `PlayModeConfig.asset`** — ScriptableObject tuning Gameplay generation (includeTransforms/Components/LayerStats, maxDepth, excludeLayers).

## 4. Protocols & data flow
- **MCP→Unity**: `{"action": "...", "request_id": "uuid", ...params}` on WS; Unity replies `{"type":"mcp_response","request_id":"...","content": "string|object"}` (for screenshots the inner `content` holds base64). Correlation via `pending_requests[requestId]` Future + `unity_request_lock` (strictly serial; extra concurrent MCP call gets busy error).
- **Gameplay loop**: `GameplayAgent` (Play Mode) every ~3 s → `BridgeRelay.Send` → `EditorBridge` (hooked via `BridgeRelay.OnRequestSendToServer`) → WS `gameplay_response` with `{agentActions, sceneJson, b64Image}` → `unity_bridge.processing_lock` (drop if locked) + vision gate → `analyze_gameplay_scene(..., config)` (injected LLM) → `function_call`(s) back to Unity → `AgentCommandRelay` executes click and signals `HandleActionComplete`.
- **Lighting subagent**: triggered as MCP tool → LangGraph loop with screenshot + Unity tool results as context; produces final report string back to caller via `mcp_response`.
- **Connection**: single WS `127.0.0.1:8765` (Editor is client). Single-flight on both ends (server lock + Unity `_isProcessing`). Instance IDs invalid across domain reload/recompile — caller must re-call `get_scene_hierarchy`.

## 5. Limitations / gotchas (from README + code)
- Instance IDs unstable across recompiles/domain reloads.
- Single-flight — overlapping MCP calls queue / get busy error.
- `get_screenshot` Edit Mode only (Scene view); Play Mode uses `ScreenshotTool` path.
- `FindAssetReferences` actually returns dependencies (`GetDependencies`), not referencers despite name.
- `LightingAgent`: final synthesis can be empty if resolution is a tool-only turn; `search_unity_docs` not guaranteed to be called; 2 consecutive tool errors abort early (see `LightingAgent/README.md`).
- WS `ReceiveLoop` reassembles fragmented frames into `MemoryStream` before JSON parse; `processing_lock` for gameplay prevents backlog.

## 6. How to run
1. `uv` installed; MCP config: `uv --directory <repo>/Server run main.py` (stdio).
2. Unity: add package from disk (`com.gamenami.unity-semantic-bridge`), open `Tools > Unity Semantic Bridge > Connect to Server`.
3. No `.env` key required — injected LLM via `RunnableConfig` + vision gate covers both subagents and gameplay. Add provider keys only for your chosen model.

## 7. Repo layout quick ref
```
Server/ main.py, mcp_tools.py, unity_bridge.py, state_manager.py, capability_gate.py
        Runtime/image_analysis.py, system_prompt.txt
        LightingAgent/lighting_agent.py (+ README.md)
        pyproject.toml, uv.lock, .env
com.gamenami.../ package.json (unity 2022.3, Newtonsoft Json 3.2.2)
        Editor/ EditorBridge, McpMessageHandler, SceneFunctions, ComponentFunctions,
                AssetFunctions, LightingFunctions, MainThreadMessageQueue, RuntimeAgentHandler,
                Window/SemanticBridgeWindow, EditModeScreenshotTool, *.asmdef
        Runtime/ SemanticScene, SemanticSceneGenerator, HeuristicFilters, GameplayAgent,
                 CanvasButtonFinder, Tools/ScreenshotTool, Relays/BridgeRelay etc., Settings/
docs/ (this file), images/usb-*.png
```
