# SubAgent: Lighting Diagnostic Agent

This folder contains a LangGraph-based autonomous agent for diagnosing Unity lighting issues.

## Overview

The `LightingDiagnosticAgent` is a specialized sub-agent that can autonomously investigate and diagnose lighting problems in Unity scenes. Unlike calling individual MCP tools manually, this agent will:

1. **Iteratively investigate** - Makes multiple tool calls to gather comprehensive diagnostic data
2. **Reason about findings** - Uses Claude to analyze results and decide next steps
3. **Continue until resolved** - Keeps trying different approaches until the issue is identified or max iterations reached

## Architecture

The agent is built with **LangGraph**, which provides:
- **State management** - Tracks diagnostic progress across iterations
- **Tool integration** - Seamlessly calls Unity MCP tools
- **Conditional routing** - Decides whether to continue or end based on findings

### Workflow

```
Start → Diagnose → Call Tools → Check Resolution → Continue/End
         ↑                                            ↓
         └────────────────────────────────────────────┘
```

## Usage

The agent is exposed as an MCP tool: `diagnose_lighting_issue`

```python
# Example from Claude Code
result = await diagnose_lighting_issue(
    instance_id=12345,  # GameObject with lighting issues
    issue_description="Object appears completely black despite nearby lights",
    max_iterations=10
)
```

### Parameters

- `instance_id` (int): Unity GameObject instance ID (get from `get_scene_hierarchy`)
- `issue_description` (str): Human-readable description of the lighting problem
- `max_iterations` (int, optional): Maximum diagnostic loops (default: 10)

### Available Unity Tools

The agent has access to:
- `get_lights_affecting_object` - Inspect all lights near the object
- `get_urp_pipeline_settings` - Check render pipeline configuration
- `get_component_inspector_values` - Inspect renderer/material settings
- `inspect_gameobject` - Get full GameObject details

## Example Diagnostic Flow

1. **Iteration 1**: Check what lights are in range
2. **Iteration 2**: Check URP settings for per-object light limits
3. **Iteration 3**: Inspect GameObject's layer and rendering layer mask
4. **Iteration 4**: Check material shader and properties
5. **Resolution**: Identifies that object is on wrong rendering layer mask

## Adding New Agents

To add more specialized agents:

1. Create new agent file in `SubAgent/` (e.g., `physics_diagnostic_agent.py`)
2. Follow the same LangGraph pattern with StateGraph
3. Register as MCP tool in `mcp_tools.py`
4. Add dependencies to `pyproject.toml` if needed

## Dependencies

- `langgraph` - Agent orchestration framework
- `langchain-anthropic` - Claude integration
- `langchain-core` - Core abstractions

## Environment Variables

Add to `Server/.env`:
```
ANTHROPIC_API_KEY=your-api-key-here
```

The agent uses `claude-sonnet-4-5-20250929` by default.
