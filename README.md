# Unity Semantic Bridge (USB)

![Static Badge](https://img.shields.io/badge/unity_version-%3E%3D%202022.3-blue)
![Static Badge](https://img.shields.io/badge/unity_version-6000.3%20%3C%3D-blue)
![Static Badge](https://img.shields.io/badge/runtime-uv-purple)

A Unity MCP Bridge built for agents working alongside you in a live Editor session — not just querying a static project. Token-efficient tools give your agent visibility into what changed in the scene while you were both working in it.

<img src="images/usb-editor-mode.png" alt="Alt text" width="600">

## Features

- Optimized scene heirarchy query and gameobject inspection tools with workflow hints for agents - more token efficient than official Unity CLI tool
- In Editor connection status and reconnect. Not tied to Unity Licensing - Runs 100% local. No need to reauthenticate when token expires
- Dedicated tools for agent to see human actions and changes
- In Editor MCP log so you can see what tools are called
- Dedicated Lighting tools for URP projects

## Prerequesites

 - **Unity 2022.3 LTS up to Unity 6.3** 
	- uses `UnityEditor.ObjectChangeEvents.changesPublished` available in 2022.3 LTS and later
	- versions of Unity newer than 6.3 use change `InstanceIds` for `EntityIds` - not tested for compatibility yet
- **uv** (https://docs.astral.sh/uv/getting-started/installation/)

####  Optional
- **API key for LLM with strong vision capabiltiies** - lighting subagent can be powered by a separate LLM (ideally with vision-in-the-loop like Fable 5 or Kimi K3); see `/core/llm_provider.py`. Tools are still available to your main/orchestrator agent even without setting up the subagent. Previously used mcp sampling but this was deprecated. 

## Installation

1. Clone this project.
2. Add the Unity package in `/com.gamenami.unity-semantic-bridge` to your Unity project via "add package from disk".
3. You should have `uv` installed so it can run and automatically update the server's dependencies. Add the MCP Server in `/mcp-editor-bridge` to your preferred IDE Agent's list of MCP servers. The MCP server POSTs to the Unity Editor's HTTP listener at `http://127.0.0.1:1073/mcp`.

```json
"mcpServers": {
    "unity-semantic-bridge": {
		"command": "uv",
		"args": [
			"--directory", "<YOUR_LOCAL_PATH_TO_/unity-semantic-bridge/mcp-editor-bridge>",
			"run", "main.py"
		]
    }
}
```

4. In Unity, from the Tools menu, select "Unity Semantic Bridge" > "Start HTTP Listener" (port 1073). Your IDE Agent now has access to your Unity Project and MCP tools provided. Health check: `GET http://127.0.0.1:1073/health`.

## Available Tools

**Human-Agent Cowork**
- `get_recent_unity_events` - Returns Unity Editor events (gameObject/hierarchy/selection/play-mode/console changes)
- `get_project_settings` - Allows agent to orientate itself to the project

**Scene & GameObject inspection**
- `get_scene_hierarchy` — Returns the scene as a list of GameObjects with paths and instance IDs. Usually the first tool to call. Supports depth limiting, a node cap with truncation reporting (for large scenes), and optional filtering to only main-camera-visible objects.
- `get_gameobject_tree` — Full hierarchy under a specific GameObject.
- `inspect_gameobject` — Full details for a single GameObject.
- `get_component_inspector_values` — Inspector values for a specific component on a GameObject.
- `get_component_code` — Source code for a component.
- `add_component` / `remove_component`
- `set_field_values` — Modify field values on a GameObject.

**Lighting**
- `get_lights_affecting_object` — Lights within range of a target object, using distance to the object's actual bounds (not just its transform pivot) — important for large/flat objects like terrain.
- `get_urp_pipeline_settings` — Current URP render pipeline configuration, including active Rendering Path (Forward / Forward+ / Deferred) and per-object light limits.
- `diagnose_lighting_issue` — Autonomous multi-step diagnostic subagent for lighting problems. See `LightingAgent/README.md` for details.

**Assets & project**
- `find_asset_references` — Finds all assets/scenes referencing a specific asset path.
- `find_unity_files` — Finds assets matching a query.
- `get_project_tree` — Project folder structure.
- `write_unity_script` — Creates/writes a C# script file.
- `get_compilation_status` - Allows agent to check if there's a domain reload

**Editor & runtime**
- `get_screenshot` — Captures the Scene view (Edit Mode) as a JPEG.
- `set_unity_play_mode` — Enter/exit Play Mode.
- `get_unity_console_logs` / `clear_unity_console_logs` — Read or clear Editor console output.
- `get_unity_physics_layers` — Physics layer/collision matrix info.
- `notify_unity` — Send a message to the Unity Editor chat window.


## Known Limitations (for Agents using this tool to read)

- **Unity instance IDs are not stable across script recompiles or domain reloads.** Any C# change invalidates previously-fetched instance IDs — re-run `get_scene_hierarchy` after recompiling before reusing an ID from an earlier call.
- **The Editor connection is single-flight.** Only one MCP request is processed at a time; overlapping calls queue rather than run concurrently, since Unity's Editor-side message handling is inherently serial.
