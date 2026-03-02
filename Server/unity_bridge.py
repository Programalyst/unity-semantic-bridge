import logging
import json
import asyncio
from image_analysis import gemini_image_analysis
from scene_analysis import gemini_scene_analysis
from state_manager import AppState, app_state

logger = logging.getLogger(__name__)
# prevent 1002 protocol error due to Unity sending a large buffer (sceneJson+image) with a Continuation Frame 
# without an Initial Frame, or it might not set the "FIN" (Final) bit correctly
processing_lock = asyncio.Lock() 

async def handle_unity_message(websocket, payload_string):
    """
    Main logic hub: Parses JSON, calls Gemini, and sends tools back.
    """
    data = json.loads(payload_string)
    msg_type = data.get("type")

    # Message from Unity -> Respond to MCP
    if msg_type == "mcp_response":
        if app_state.unity_res_future and not app_state.unity_res_future.done():
            app_state.unity_res_future.set_result(data)
        return
    
    # Message from Unity -> Gemini image analysis -> back to Unity
    if msg_type == "chat":
        user_text = data.get("message")
        scene_json = data.get("scene")
        
        logging.info(f"💬 [Chat Request] {user_text}")
        
        response_text = await gemini_scene_analysis(user_text, scene_json)
        
        await websocket.send(json.dumps({
            "type": "chat_response",
            "content": response_text
        }))
        return
    
    # PLAY MODE LOOP: Message from Unity -> Gemini image analysis -> back to Unity
    # Only apply the lock here to prevent frame-spamming
    if processing_lock.locked():
        return

    async with processing_lock: # will clear the lock after code finishes
        try:
            data = json.loads(payload_string)
            scene_json = data.get("sceneJson")
            b64_image = data.get("b64Image")

            logger.info(f"Processing Scene {len(str(scene_json))} chars + Image {len(str(b64_image))} chars")

            response = await gemini_image_analysis(scene_json, b64_image)

            if response:
                await handle_gemini_response(websocket, response)
            
            # Let the socket clear
            await asyncio.sleep(0.1) 

        except json.JSONDecodeError:
            logging.error("Failed to decode JSON from Unity.")
        except Exception as e:
            logging.error(f"Error in payload handler: {e}")

async def handle_gemini_response(websocket, gemini_response):
    """
    Parses the Gemini response and sends the command back to Unity 
    via the established MPE WebSocket.
    """
    try:
        # 1. Handle Function Calls (Tools)
        if gemini_response.function_calls:
            # Gemini SDK's function_calls can be converted to dicts
            calls = [fc.to_json_dict() for fc in gemini_response.function_calls]
            
            # ---- For debugging ----
            for call in calls:
                name = call.get('name')
                args = call.get('args', {})
                
                if name == 'click_screen_position':
                    x, y = args.get('screenX'), args.get('screenY')
                    intent = args.get('Intent', 'No intent provided')
                    logger.info(f"🎯 [TOOL] click_screen_position: ({x}, {y}) | Intent: {intent}")
                elif name == 'press_ui_button':
                    btn = args.get('ButtonName')
                    logger.info(f"🔘 [TOOL] press_ui_button: {btn}")
            # ---- ----

            payload = {
                "type": "function_call", 
                "content": calls
            }
            
        # 2. Handle Plain Text Response
        else:
            payload = {
                "type": "text",
                "content": gemini_response.text
            }
            logger.info(f"🤖 [TEXT]: {gemini_response.text}")

        # 3. Send over MPE (Unity MPE handles the 4-byte header on receipt)
        await websocket.send(json.dumps(payload))
        
    except Exception as e:
        logging.error(f"Failed to send tool call to Unity: {e}")