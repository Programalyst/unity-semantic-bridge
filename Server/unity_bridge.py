import logging
import json
import asyncio
from state_manager import app_state
from Runtime.image_analysis import gemini_image_analysis

logger = logging.getLogger(__name__)
# prevent 1002 protocol error due to Unity sending a large buffer (sceneJson+image) with a Continuation Frame 
# without an Initial Frame, or it might not set the "FIN" (Final) bit correctly
processing_lock = asyncio.Lock() 

async def forward_to_unity(payload: dict) -> str:
    """Helper to handle the Request-Response loop to Unity."""
    if not app_state.unity_ws:
        return "Error: Unity Editor is not connected to the bridge."
    
    app_state.unity_res_future = asyncio.get_event_loop().create_future()
    await app_state.unity_ws.send(json.dumps(payload))
    
    try:
        result = await asyncio.wait_for(app_state.unity_res_future, timeout=20.0)
        return str(result)
    except asyncio.TimeoutError:
        return "Error: Unity timed out responding to the request."
    finally:
        app_state.unity_res_future = None

async def handle_unity_message(payload_string):

    data = json.loads(payload_string)
    msg_type = data.get("type")

    # Message from Unity -> Respond to MCP
    if msg_type == "mcp_response":
        if app_state.unity_res_future and not app_state.unity_res_future.done():
            app_state.unity_res_future.set_result(data)
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

                gemini_response = await gemini_image_analysis(
                    agent_actions=actions_data,
                    b64_image=image_data,
                    scene_json=scene_data
                ) 

                # extract function calls
                if gemini_response.function_calls:
                # Gemini SDK's function_calls can be converted to dicts
                    calls = [fc.to_json_dict() for fc in gemini_response.function_calls]
                
                payload = json.dumps({
                    "type": "function_call", 
                    "content": calls
                })
                await app_state.unity_ws.send(payload)

            except json.JSONDecodeError:
                logging.error("Failed to decode JSON from Unity.")
            except Exception as e:
                logging.error(f"Error in payload handler: {e}")
