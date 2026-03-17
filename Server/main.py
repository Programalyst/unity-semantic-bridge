import asyncio
import os
import sys
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
    app_state.unity_ws = websocket
    logging.info("✅ Unity Bridge connected.")
    
    try:
        async for message in websocket:
            await handle_unity_message(message)
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
        8765,
        reuse_address=True # don't block off the port if Unity reconnects quickly
    ):
        logging.info("🚀 Bridge Server listening on 8765...")
    
        # Simply run the MCP server allowing it to control the lifecycle
        # When Claude closes the pipe, this task finishes, 
        # the 'async with' block exits, and the process dies naturally.
        await mcp.run_stdio_async()

    logging.info("🔌 MCP Server stopped. Cleaning up...")

if __name__ == "__main__":
    asyncio.run(run_servers())