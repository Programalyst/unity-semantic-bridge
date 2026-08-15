"""
Gameplay image analysis — uses the caller's LLM via RunnableConfig.

Requires a vision-capable client (checked via capability_gate) and an LLM
injected as config["configurable"]["llm"] (a LangChain BaseChatModel).
"""
import base64
import json
import logging
from typing import Optional, Any

from langchain_core.runnables import RunnableConfig
from core.llm_provider import get_llm_from_config
from langchain_core.language_models import BaseChatModel
from langchain_core.tools import tool
from langchain_core.messages import HumanMessage, SystemMessage, AIMessage
from langchain_core.outputs import ChatGeneration, ChatResult

from state_manager import app_state
from capability_gate import supports_vision, get_client_capabilities

logger = logging.getLogger(__name__)

# Load system prompt once
system_prompt_path = app_state.base_dir / "Runtime/system_prompt.txt"
try:
    system_prompt = system_prompt_path.read_text(encoding="utf-8")
except Exception as e:
    logger.warning(f"Failed to load system prompt: {e}")
    system_prompt = "You are a gameplay agent."

# --- LangChain tools for gameplay ---
@tool
def click_ui_button(ButtonName: str, Intent: str, AncestorName: Optional[str] = None) -> str:
    """Click a button with the specified button name."""
    return f"click_ui_button {ButtonName} intent={Intent}"

@tool
def click_screen_position(screenX: float, screenY: float, Intent: str) -> str:
    """Clicks the provided screen position in pixels. 0x, 0y is the top left of the screen."""
    return f"click_screen_position {screenX},{screenY} intent={Intent}"

GAMEPLAY_TOOLS = [click_ui_button, click_screen_position]

async def analyze_gameplay_scene(
    agent_actions: list[str],
    scene_json: Any,
    b64_image: str,
    config: RunnableConfig | None = None,
) -> AIMessage:
    """
    Analyze gameplay screenshot + scene JSON using the injected user's LLM.

    Requires vision support and a RunnableConfig with llm.
    Returns an AIMessage that may contain tool_calls for click actions.
    """
    # Vision gate — gameplay is inherently vision-based
    # If we have captured client capabilities and they indicate text-only, fail early
    caps = get_client_capabilities()
    if caps is not None and not supports_vision(caps):
        raise RuntimeError(
            "Vision not supported — gameplay analysis requires a vision-capable client. "
            f"Current capabilities {caps} indicate text-only. "
            "The gameplay agent's image analysis cannot run without image support."
        )

    llm = get_llm_from_config(config)

    # Prepare semantic context
    semantic_context = json.dumps(scene_json, indent=2) if not isinstance(scene_json, str) else scene_json
    actions = "\n".join(agent_actions) if agent_actions else "(no prior actions)"

    prompt_text = f"""### Past actions taken by agent:
{actions}

### Scene Data in JSON:
{semantic_context}

### Task:
Analyze the image and the JSON. Identify the best tactical move. 
Use the `viewportPos` and `path` from the JSON to identify targets.
"""

    # Build messages with image
    # Use data URL for image
    image_url = f"data:image/jpeg;base64,{b64_image}"
    messages = [
        SystemMessage(content=system_prompt),
        HumanMessage(content=[
            {"type": "text", "text": prompt_text},
            {"type": "image_url", "image_url": {"url": image_url}},
        ]),
    ]

    # Bind tools and invoke
    # Use tool_choice="any" equivalent — langchain bind_tools will allow any tool
    # For strict "ANY" mode, we can pass tool_choice="any" if LLM supports it
    try:
        llm_with_tools = llm.bind_tools(GAMEPLAY_TOOLS, tool_choice="any")  # type: ignore[arg-type]
    except TypeError:
        # Fallback if LLM doesn't support tool_choice="any"
        llm_with_tools = llm.bind_tools(GAMEPLAY_TOOLS)

    # Prefer async
    if hasattr(llm_with_tools, "ainvoke"):
        response = await llm_with_tools.ainvoke(messages)  # type: ignore
    else:
        response = llm_with_tools.invoke(messages)  # type: ignore

    # Normalize to AIMessage
    if isinstance(response, AIMessage):
        logger.info(f"Gameplay analysis response: tool_calls={getattr(response, 'tool_calls', None)} content={str(response.content)[:200]}")
        return response
    # Some LLMs return ChatResult
    if isinstance(response, ChatResult):
        msg = response.generations[0].message
        if isinstance(msg, AIMessage):
            return msg
        return AIMessage(content=str(msg))
    # Fallback string
    return AIMessage(content=str(response))
