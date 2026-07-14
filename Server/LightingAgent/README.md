# SubAgent: Lighting Diagnostic Agent

This folder contains a LangGraph-based autonomous agent for diagnosing Unity lighting issues.

## Overview

The `LightingDiagnosticAgent` is a specialized sub-agent that can autonomously investigate and diagnose lighting problems in Unity scenes. Unlike calling individual MCP tools manually, this agent will:

1. **See the scene** - Automatically captures a Scene View screenshot at the start of every diagnosis and includes it as visual context alongside the text description
2. **Iteratively investigate** - Makes multiple tool calls to gather comprehensive diagnostic data
3. **Reason about findings** - Uses Gemini to analyze results and decide next steps
4. **Verify assumptions** - Uses grounded web search to check Unity/URP behavior it isn't certain about, rather than relying solely on parametric knowledge (which can be stale or wrong for engine-version-specific details)
5. **Continue until resolved** - Keeps trying different approaches until the issue is identified, stops early after 2 consecutive tool errors (rather than burning through remaining iterations against a broken call), or max iterations is reached

## Architecture

The agent is built with **LangGraph**, which provides:
- **State management** - Tracks diagnostic progress across iterations (via an `add_messages`-annotated state so tool results and follow-up reasoning correctly accumulate rather than overwrite each other)
- **Tool integration** - Seamlessly calls Unity MCP tools
- **Conditional routing** - Decides whether to continue or end based on findings, and skips the tool-execution step entirely on turns where the model responds with plain text rather than a tool call

### Workflow

```
Start → Diagnose ─┬─(tool call)──→ Call Tools ──→ Check Resolution ─┬─(continue)─→ Diagnose
                   └─(no tool call)──────────────→ Check Resolution ─┴─(end)──────→ Done
```

## Usage

The agent is exposed as an MCP tool: `diagnose_lighting_issue`

```python
# Example from Claude Code
result = await diagnose_lighting_issue(
    instance_id=12345,  # GameObject with lighting issues
    issue_description="Object appears completely black despite nearby lights",
    max_iterations=5
)
```

**Tip:** for "light not affecting an object" style issues, pass the instance ID of the **affected object** (e.g. the ground/surface not receiving light), not the light itself. `get_lights_affecting_object` computes distance to the target's actual mesh/collider bounds, which is what determines whether a given light is in range — calling it on the light alone won't tell you whether a *specific* object is within that light's effective reach.

### Parameters

- `instance_id` (int): Unity GameObject instance ID (get from `get_scene_hierarchy`) — see tip above for which object to target
- `issue_description` (str): Human-readable description of the lighting problem. Include any observed *pattern* (e.g. "inconsistent," "depends on camera angle," "only some objects affected") — this materially helps the agent favor root causes consistent with the actual symptom rather than the first plausible-sounding cause it finds
- `max_iterations` (int, optional): Maximum diagnostic loops (default: 5)

### Available Unity Tools

The agent has access to:
- `get_lights_affecting_object` - Inspect all lights near the object, using distance to the object's actual bounds (not just its transform pivot) — important for large/flat objects like terrain or ground planes
- `get_urp_pipeline_settings` - Check render pipeline configuration, including the active Rendering Path (Forward / Forward+ / Deferred) and any per-object light limits
- `get_component_inspector_values` - Inspect renderer/material settings
- `inspect_gameobject` - Get full GameObject details
- `search_unity_docs` - Grounded web search for verifying Unity/URP behavior. Runs as its own isolated tool call (Gemini doesn't allow combining native search grounding with function calling in the same request), so this is backed by a separate grounded-only LLM instance rather than being bound directly alongside the tools above

## Example Diagnostic Flow

Based on a real diagnosed issue: a point light near a large ground plane appeared to inconsistently light the ground depending on camera angle and which other lights were nearby.

1. **Iteration 1**: Check what lights are in range of the ground object — finds 11 lights within range of a single large plane
2. **Iteration 2**: Check URP settings for rendering configuration — finds Rendering Path is `Forward` with a `Per-Object Light Limit` of 4
3. **Iteration 3**: Use `search_unity_docs` to verify how Forward's per-object limit actually behaves and whether alternative configurations avoid it
4. **Resolution**: Root cause is Forward rendering's hard per-object light cap — with 11 lights competing for 4 slots, the specific light in question is frequently excluded from the "top N" selection, and which lights make the cut shifts with camera/position, explaining the inconsistent symptom. Recommendations include both tactical fixes (raise the limit, reduce nearby light count) and the more fundamental fix (switch to Forward+, which removes the per-object cap entirely)

## Known Limitations

- **The diagnostic loop can end without a final synthesis if resolution happens via a tool-call-only turn.** The `check_resolution` step captures its own explanation into message history specifically to guard against this, but if you see an empty or truncated final report, this is the first place to look.
- **`search_unity_docs` isn't guaranteed to be called.** The system prompt instructs the agent to verify at least one assumption via search, but tool invocation is still a model decision — if the model is confident (even if wrong) from data already in its context, it may not reach for it. Worth spot-checking agent logs (`[search_unity_docs] Query: ...`) if a diagnosis seems to rely on an assumption you're not sure the model actually verified.
- **Unity instance IDs are not stable across script recompiles/domain reloads.** Any C# change during a diagnostic session invalidates previously-fetched instance IDs. Always re-fetch via `get_scene_hierarchy` after a recompile before calling `diagnose_lighting_issue`.
- **Two consecutive tool errors (e.g. "GameObject not found" from a stale instance ID) will end the session early**, before `max_iterations` is reached. This is intentional — it avoids burning iterations against a broken call — but it means a single bad instance ID early in a session can end diagnosis faster than you might expect. Check `consecutive_tool_errors` reasoning in the logs if a session ends unexpectedly early.

## Adding New Agents

To add more specialized agents:

1. Create new agent file in `SubAgent/` (e.g., `physics_diagnostic_agent.py`)
2. Follow the same LangGraph pattern with StateGraph, including the `add_messages` reducer on the message state field and a `tools_condition`-based conditional edge out of the reasoning node
3. Register as MCP tool in `mcp_tools.py`
4. Add dependencies to `pyproject.toml` if needed

## Dependencies

- `langgraph` - Agent orchestration framework
- `langchain-google-genai` - Gemini integration
- `langchain-core` - Core abstractions

## Environment Variables

Add to `Server/.env`:
```
GOOGLE_API_KEY=your-api-key-here
```

The agent uses `gemini-2.5-flash` by default, with `temperature=0`.
