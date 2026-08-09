import logging
import json
import asyncio
import uuid
from state_manager import app_state

logger = logging.getLogger(__name__)
# prevent 1002 protocol error due to Unity sending a large buffer (sceneJson+image) with a Continuation Frame 
# without an Initial Frame, or it might not set the "FIN" (Final) bit correctly
processing_lock = asyncio.Lock() 

async def forward_to_unity(payload: dict) -> str:
    if not app_state.unity_ws:
        return "Error: Unity Editor is not connected to the bridge."
    
    async with app_state.unity_request_lock: # queue all Unity calls. Otherwise a backlog of calls will slow Unity down.
        request_id = str(uuid.uuid4())
        payload["request_id"] = request_id  # Unity must echo this back
        
        future = asyncio.get_event_loop().create_future()
        app_state.pending_requests[request_id] = future  # add future to dict
        
        await app_state.unity_ws.send(json.dumps(payload))
        
        try:
            result = await asyncio.wait_for(future, timeout=60.0)
            return str(result)
        except asyncio.TimeoutError:
            return "Error: Unity timed out responding to the request."
        finally:
            app_state.pending_requests.pop(request_id, None)


async def fetch_screenshot_base64() -> str:
    """Returns raw base64 JPEG string from Unity's Game/Scene view."""
    if not app_state.unity_ws:
        raise RuntimeError("Unity Editor is not connected to the bridge.")

    request_id = str(uuid.uuid4())
    payload = {"action": "Get_Screenshot", "request_id": request_id}
    future = asyncio.get_event_loop().create_future()
    app_state.pending_requests[request_id] = future

    async with app_state.unity_request_lock:
        await app_state.unity_ws.send(json.dumps(payload))
        try:
            result = await asyncio.wait_for(future, timeout=60.0)
            return result.get("content", "")  # extract just the base64 payload, not the whole dict
        finally:
            app_state.pending_requests.pop(request_id, None)


async def handle_unity_message(payload_string):
    logger.info(f"📥 RAW from Unity: {payload_string[:50]}")

    data = json.loads(payload_string)
    msg_type = data.get("type")

    # Message from Unity -> Respond to MCP
    if msg_type == "mcp_response":
        request_id = data.get("request_id")
        future = app_state.pending_requests.get(request_id)
        if future and not future.done():
            future.set_result(data)
        return
    
    # Message from Unity -> Send to GameplaySubAgent
    # Only apply the lock here to prevent frame-spamming
    if msg_type == "gameplay_response":
        if processing_lock.locked():
            return

        async with processing_lock: # will clear the lock after code finishes
            try:
                data = json.loads(payload_string)
                content = data.get("content", {})

                actions_data = content.get("agentActions", [])
                scene_data = content.get("sceneJson")
                image_data = content.get("b64Image")

                #logger.info(f"Received Scene {len(str(scene_data))} chars + Image {len(str(image_data))} chars")

                # Vision gate for gameplay — uses same capability check as MCP vision tools
                from capability_gate import supports_vision, get_client_capabilities
                caps = get_client_capabilities()
                if caps is not None and not supports_vision(caps):
                    logger.warning("Gameplay vision gate blocked: client is text-only, skipping image analysis")
                    return

                # Use injected LLM via RunnableConfig
                from Runtime.image_analysis import analyze_gameplay_scene

                # Resolve LLM config from app_state if available, otherwise try to get from global
                gameplay_config = getattr(app_state, "gameplay_llm_config", None) or getattr(app_state, "llm_config", None)

                try:
                    response = await analyze_gameplay_scene(
                        agent_actions=actions_data,
                        b64_image=image_data,
                        scene_json=scene_data,
                        config=gameplay_config,
                    )
                except ValueError as ve:
                    # Missing LLM via RunnableConfig
                    logger.warning(f"Gameplay analysis skipped: {ve}")
                    return
                except RuntimeError as re:
                    # Vision not supported or other runtime gate
                    logger.warning(f"Gameplay analysis skipped: {re}")
                    return

                # Extract tool calls from AIMessage — routing is via msg_type, not python function names
                calls = []
                if response is not None:
                    tool_calls = getattr(response, "tool_calls", None)
                    if tool_calls:
                        for tc in tool_calls:
                            if isinstance(tc, dict):
                                calls.append({"name": tc.get("name"), "args": tc.get("args", {})})
                            else:
                                calls.append({"name": getattr(tc, "name", None), "args": getattr(tc, "args", {})})
                
                # If no tool calls, nothing to send (agent may have returned text)
                if not calls:
                    logger.info(f"Gameplay analysis returned no tool calls: {getattr(response, 'content', response)}")
                    return

                payload = json.dumps({
                    "type": "function_call", 
                    "content": calls
                })
                await app_state.unity_ws.send(payload)

            except json.JSONDecodeError:
                logging.error("Failed to decode JSON from Unity.")
            except Exception as e:
                logging.error(f"Error in payload handler: {e}", exc_info=True)
