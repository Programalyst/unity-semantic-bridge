import asyncio
import websockets
import logging
import os
import json
from unityBridge import handle_unity_payload

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')

async def connect_to_unity_mpe(unity_port):
    """This is the persistent data channel."""
    uri = f"ws://127.0.0.1:{unity_port}/sus-agent-channel"
    try:
        # ws frame max_size = 10 MB to accomodate screenshot + sceneJson. Increase as needed
        async with websockets.connect(uri, 
                                      max_size=10_000_000,
                                      ping_interval=None, # Disable pings to prevent timeout during long Gemini calls
                                      compression=None    # Disable compression to prevent frame mismatch
                                      ) as websocket:
            logging.info(f"Connected to Unity MPE on port {unity_port}")
            
            async for message in websocket:
                # 1. Strip Unity's 4-byte ID header
                payload = message[4:] if isinstance(message, bytes) else message
    
                # 2. GUARD: If it doesn't look like JSON, just ignore it silently
                if not payload or not str(payload).strip().startswith('{'):
                    # "Catch" numeric Unity ClientID and ignore without an error
                    continue 

                await handle_unity_payload(websocket, payload)

    except Exception as e:
        logging.error(f"Failed to connect to Unity MPE: {e}")

async def handle_handshake(websocket):
    """This handles the initial 'mpe_init' from Unity."""
    try:
        async for message in websocket:
            data = json.loads(message)
            if data.get("type") == "mpe_init":
                unity_port = data.get("port")
                logging.info(f"Received Unity MPE port: {unity_port}")

                await asyncio.sleep(1.0) # Wait for Unity to start MPE channel
                
                # Start the MPE client as a separate task so this handler can finish cleanly
                asyncio.create_task(connect_to_unity_mpe(unity_port))
                
                await websocket.send(json.dumps({"status": "ok"}))
                return # close the handshake socket
            
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