import asyncio
import json
import logging
from Runtime.image_analysis import gemini_image_analysis
from state_manager import app_state

logger = logging.getLogger(__name__)

class GameplaySubAgent:
    def __init__(self):
        self.action_history = []  # Gameplay-specific history
        self.max_steps = 15 # Safety limit per tool call
    
    async def run_autonomous_mission(self, goal: str):
        self.action_history.append({"role": "user", "content": f"New Mission: {goal}"})
        
        for step in range(self.max_steps):
            # A: SENSE - Create a future and wait for Unity to send the next frame
            app_state.gameplay_future = asyncio.get_event_loop().create_future()

            # Actively request the first frame ---
            if app_state.unity_ws:
                await app_state.unity_ws.send(json.dumps({
                    "type": "get_gameplay_frame" 
                }))
            
            try:
                # Wait for handle_unity_message to resolve this
                payload = await asyncio.wait_for(app_state.gameplay_future, timeout=10.0)
                
                # B: THINK - Call Gemini
                gemini_response = await gemini_image_analysis(
                    history=self.action_history,
                    image=payload['b64Image'],
                    scene_data=payload['sceneJson']
                )

                # extract function calls
                if gemini_response.function_calls:
                # Gemini SDK's function_calls can be converted to dicts
                    calls = [fc.to_json_dict() for fc in gemini_response.function_calls]
                
                # C: ACT - Send full gemini response to Unity
                payload = {
                    "type": "function_call", 
                    "content": calls
                }
                await app_state.unity_ws.send(payload)
                
                # 4. EXIT CHECK: Did we reach the goal?
                if gemini_response.get('status') == 'DONE':
                    return f"Mission Success: {gemini_response['summary']}"
            
                # Wait for Unity physics/animations to play out
                await asyncio.sleep(2) 

                return "Mission timed out: Goal not reached within max steps."
            
            finally:
                app_state.gameplay_future = None
    

    async def handle_gemini_response(self, gemini_response):
        """
        Parses the Gemini response and sends the command back to Unity 
        """
        try:
            # 1. Handle Function Calls
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

            # 3. Send to Unity
            #await forward_to_unity(payload) # cannot use forward to Unity as the editor path future awaits the response
            
        except Exception as e:
            logging.error(f"Failed to send tool call to Unity: {e}")