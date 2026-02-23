This summary covers the development of the Semantic Unity Scene package. It is a bridge between Unity and LLMs, specifically Google Gemini, for "Spatial Intelligence." 

Core Architecture: "Sense-Think-Act"
The system is designed as a modular bridge that enables an LLM to "see" the game (Vision) and "understand" its logic (Hierarchy/Components).

Sensing (Unity -> Python):

SemanticSceneGenerator.cs: Generates a "Semantic JSON." It uses Heuristic Filters to remove "engine noise" while keeping gameplay-significant objects.

ScreenshotTool.cs: Captures a compressed JPG.

SemanticSceneConfigSo: A ScriptableObject-based configuration system allowing different settings for Editor Mode and Play Mode.

Thinking (Python Server):

MPE Bridge: Uses Unity’s internal Multi-Process Engine (MPE) to communicate between the Unity Editor and a local Python server.

Gemini Integration: Combines the Scene JSON and Screenshot into a single prompt. The model is forced into Function Calling mode (ANY) to interact with the world via specific tools.

Editor Mode: A dedicated "Dev Chat" mode where the LLM acts as a Senior Unity Developer, analyzing the hierarchy to provide architectural advice.

Acting (Python -> Unity):

AgentCommandRelay.cs: A Runtime relay that decouples the package from game-specific code. It receives Viewport (0-1) coordinates or Button Names from Python.

EditorMpeBridge.cs: Handles the low-level socket communication, strips Unity's 4-byte MPE headers, and dispatches commands to the Main Thread via EditorApplication.delayCall.

GameLink.cs: The project-specific adapter that translates normalized coordinates into game actions.
Current Implementation Status

Stable Communication: The websocket 1002 Protocol Errors have been resolved. Look at the websockets.connect parameters in main.py. Also the reconnection logic in EditorMpeBridge (especially the RebindEvents method)

Unified UI: The SemanticSceneWindow features a side-by-side configuration for Editor/Play modes and a persistent Integrated Chat UI with Rich Text support.

Self-Healing: The bridge automatically re-synchronizes its MPE channels and event subscriptions when the Unity Editor recompiles or the window is reopened.

Next Steps:

Agent Context Protocol (ACP) Integration: Allow other IDE agents such as Codex to call the Unity Semantic Bridge to get context of the Unity scene.

Model Context Protocol (MCP) Integration: Formally wrap this system as a Model Context Protocol (MCP) tool so the IDE agent can query the live Unity scene while writing C# code.

Key Files to Inspect:
SemanticSceneGenerator.cs (Hierarchy to JSON logic)
SemanticSceneWindow.cs (Editor UI & Chat)
EditorMpeBridge.cs (Socket & Handshake logic)
HeuristicFilters.cs (Culling & Significance logic)