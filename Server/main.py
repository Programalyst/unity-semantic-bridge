import asyncio
import websockets
import logging
import json
from unity_bridge import handle_unity_message
from state_manager import AppState, app_state

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')

async def handle_mcp_request(websocket, message):
    """Handles requests coming from the MCP Server agent."""
    
    if app_state.unity_ws:
        # Create a Future to await the response from Unity
        app_state.unity_res_future = asyncio.get_event_loop().create_future()
        await app_state.unity_ws.send(message)
        
        # Wait for Unity to send response (handled in EditorMpeBridge)
        try:
            # 5-second timeout in case Unity is busy
            result = await asyncio.wait_for(app_state.unity_res_future, timeout=5.0)
            await websocket.send(json.dumps({"type": "unity_mcp_result", "content": result}))
        except asyncio.TimeoutError:
            await websocket.send(json.dumps({"error": "Unity timeout"}))
        finally:
            app_state.unity_res_future = None # Cleanup
    else:
        await websocket.send(json.dumps({"error": "Unity not connected"}))

async def connect_to_unity_mpe(unity_port):
    """This is the persistent data channel."""

    uri = f"ws://127.0.0.1:{unity_port}/usb-agent-channel"
    try:
        # ws frame max_size = 10 MB to accomodate screenshot + sceneJson. Increase as needed
        async with websockets.connect(uri, 
                                      max_size=10_000_000,
                                      ping_interval=None, # Disable pings to prevent timeout during long Gemini calls
                                      compression=None    # Disable compression to prevent frame mismatch
                                      ) as websocket:
            app_state.unity_ws = websocket # Store for the relay
            logging.info(f"Connected to Unity MPE on port {unity_port}")
            
            async for message in websocket:
                # 1. Strip Unity's 4-byte ID header
                payload = message[4:] if isinstance(message, bytes) else message
    
                # 2. GUARD: If it doesn't look like JSON, just ignore it silently
                if not payload or not str(payload).strip().startswith('{'):
                    # "Catch" numeric Unity ClientID and ignore without an error
                    continue 

                await handle_unity_message(websocket, payload)
                
    except Exception as e:
        logging.error(f"Failed to connect to Unity MPE: {e}")
    finally:
        app_state.unity_ws = None

async def handle_handshake(websocket):
    """This handles the initial 'mpe_init' from Unity."""
    try:
        async for message in websocket:
            data = json.loads(message)

            # ROUTE A: Unity Handshake
            if data.get("type") == "mpe_init":
                unity_port = data.get("port")
                logging.info(f"Received Unity MPE port: {unity_port}")
                await asyncio.sleep(1.0) # Wait for Unity to start MPE channel
                # Start the MPE client as a separate task so this handler can finish cleanly
                asyncio.create_task(connect_to_unity_mpe(unity_port))
                await websocket.send(json.dumps({"status": "ok"}))
                return # close the handshake socket

            # ROUTE B: MCP Agent Request
            else:
                await handle_mcp_request(websocket, message)
            
    except Exception as e:
        logging.error(f"Handshake error: {e}")

async def main():
    
    async with websockets.serve(handle_handshake, "127.0.0.1", 8765):
        await asyncio.Future()  # keeps the server running

if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logging.info("Server shutting down...")