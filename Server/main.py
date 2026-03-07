import asyncio
import json
import logging
import websockets
from mcp.server.fastmcp import FastMCP

from mcp_tools import register_unity_tools
from state_manager import app_state
from unity_bridge import handle_unity_message

# --- CONFIG & STATE ---
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
mcp = FastMCP("UnitySceneSubAgent")
register_unity_tools(mcp)

# --- WEBSOCKET BRIDGE ---
async def handle_unity_connection(websocket):
    """The new primary persistent channel for Unity."""
    app_state.unity_ws = websocket
    logging.info("✅ Unity connected directly via WebSocket.")
    
    try:
        async for message in websocket:
            # Note: RAW ClientWebSocket doesn't have the 4-byte UMPE prefix!
            # So we don't need 'payload = message[4:]' anymore.
            await handle_unity_message(websocket, message)
    except websockets.ConnectionClosed:
        logging.info("🔌 Unity disconnected.")
    finally:
        app_state.unity_ws = None

# --- MAIN ENTRY POINT ---
async def run_servers():
    # Use the context manager to ensure the WebSocket server stops on exit
    async with websockets.serve(
        handle_unity_connection, 
        "127.0.0.1", 
        8765
    ):
        logging.info("🚀 Bridge Server listening on 8765...")
    
        await asyncio.gather(
            mcp.run_stdio_async(),
            asyncio.Future()  # Keeps the bridge open while MCP runs
        )

if __name__ == "__main__":
    asyncio.run(run_servers())