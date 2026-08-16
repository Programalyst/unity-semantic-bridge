# Unity Semantic Bridge (USB)

![Static Badge](https://img.shields.io/badge/unity_version-2022.x-blue)
![Static Badge](https://img.shields.io/badge/unity_version-6000.3%20%3C%3D-blue)
![Static Badge](https://img.shields.io/badge/runtime-uv-purple)


A MCP server to allow AI coding tools (like Cursor, Claude, etc.) to query and understand Unity scenes efficiently (reduced the number of tool calls required).

<img src="images/usb-editor-mode.png" alt="Alt text" width="600">

## Features

- Optimized scene heirarchy query and gameobject inspection tools with workflow hints for agents - more token efficient than official Unity CLI tool
- In Editor connection status and reconnect. Not tied to Unity Licensing - Runs 100% local. No need to reauthenticate when token expires
- In Editor MCP log so you can see what tools are called
- Dedicated Lighting tools for URP projects

## Prerequesites

 - Unity 2022 LTS or Unity 6.3 (versions of Unity newer than 6.3 use change the InstanceIds to EntityIds - not tested for compatibility yet)
- uv (https://docs.astral.sh/uv/getting-started/installation/)
- LLM with vision capabiltiies - this MCP server will block usage with an agent/LLM that lacks vision capability

## Installation

1. Clone this project.
2. Add the Unity package in `/com.gamenami.unity-semantic-bridge` to your Unity project via "add package from disk".
3. You should have `uv` installed so it can run and automatically update the server's dependencies. Add the MCP Server in `/Server` to your preferred IDE Agent's list of MCP servers. When you start your IDE Agent (e.g. Claude Code, Codex, etc.), the MCP server will automatically start up and start listening for a connection from the Unity package.

```json
"mcpServers": {
    "unity-semantic-bridge": {
		"command": "uv",
		"args": [
			"--directory", "<YOUR_LOCAL_PATH_TO_/unity-semantic-bridge/Server>",
			"run", "main.py"
		]
    }
}
```

4. In Unity, from the Tools menu, select "Unity Semantic Bridge" > "Connect to Server". Your IDE Agent now has access to your Unity Project and MCP tools provided.

## Available Tools

**Scene & GameObject inspection**
- `get_scene_hierarchy` — Returns the scene as a list of GameObjects with paths and instance IDs. Usually the first tool to call. Supports depth limiting, a node cap with truncation reporting (for large scenes), and optional filtering to only main-camera-visible objects.
- `get_gameobject_tree` — Full hierarchy under a specific GameObject.
- `inspect_gameobject` — Full details for a single GameObject.
- `get_component_inspector_values` — Inspector values for a specific component on a GameObject.
- `get_component_code` — Source code for a component.
- `add_component` / `set_field_value` — Add components or modify field values on a GameObject.

**Lighting**
- `get_lights_affecting_object` — Lights within range of a target object, using distance to the object's actual bounds (not just its transform pivot) — important for large/flat objects like terrain.
- `get_urp_pipeline_settings` — Current URP render pipeline configuration, including active Rendering Path (Forward / Forward+ / Deferred) and per-object light limits.
- `diagnose_lighting_issue` — Autonomous multi-step diagnostic subagent for lighting problems. See `LightingAgent/README.md` for details.

**Assets & project**
- `find_asset_references` — Finds all assets/scenes referencing a specific asset path.
- `find_unity_files` — Finds assets matching a query.
- `get_project_tree` — Project folder structure.
- `write_unity_script` — Creates/writes a C# script file.

**Editor & runtime**
- `get_screenshot` — Captures the Scene view (Edit Mode) as a JPEG.
- `set_unity_play_mode` — Enter/exit Play Mode.
- `get_unity_console_logs` / `clear_unity_console_logs` — Read or clear Editor console output.
- `get_unity_physics_layers` — Physics layer/collision matrix info.
- `notify_unity` — Send a message to the Unity Editor chat window.

## Sub Agents

### Lighting Subagent
Uses the connected client's LLM via `RunnableConfig` (`config["configurable"]["llm"]`) — no separate API key. See `LightingAgent/README.md` for injection. Vision-gated: requires a vision-capable client (image sampling support) for screenshot context.

### Gameplay Subagent
Experimental; uses the same injected LLM + vision gate as the Lighting subagent (`Runtime/image_analysis.py` → `analyze_gameplay_scene`). Not ready for use yet.

### Environment Variables

No separate API key is required for the bridge or subagents — they reuse the user's connected LLM via the vision capability gate. Optional `.env` vars are only needed if your injected LLM provider requires them.


## Known Limitations (for Agents using this tool to read)

- **Unity instance IDs are not stable across script recompiles or domain reloads.** Any C# change invalidates previously-fetched instance IDs — re-run `get_scene_hierarchy` after recompiling before reusing an ID from an earlier call.
- **The Editor connection is single-flight.** Only one MCP request is processed at a time; overlapping calls queue rather than run concurrently, since Unity's Editor-side message handling is inherently serial.
- **Screenshot capture (`get_screenshot`) currently only supports Edit Mode**, capturing the Scene view camera. Play Mode capture would need a different code path (see `ScreenshotTool` vs `EditModeScreenshotTool` in the package).
