import asyncio
import logging
import websockets
from mcp.server.fastmcp import FastMCP
from mcp.server.session import ServerSession
from mcp import types

from mcp_tools import register_unity_tools
from state_manager import app_state
from unity_bridge import handle_unity_message
from capability_gate import set_client_capabilities

from dotenv import load_dotenv
load_dotenv() # Load environment variables from .env file

# --- CONFIG & STATE ---
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
mcp = FastMCP("UnitySemanticBridge")
register_unity_tools(mcp)

# --- UPFRONT FEATURE-GATING: intercept client capabilities on initialize ---
# Patch ServerSession to capture the InitializeRequestParams as soon as the client
# sends its capabilities block. This runs before any tool call, satisfying the
# "upfront" requirement. Stored in app_state for later vision checks.
_original_received_request = ServerSession._received_request

async def _patched_received_request(self, responder):  # type: ignore[no-untyped-def]
    # Capture InitializeRequest params before delegating to original handler
    try:
        # responder.request is a Request[ClientRequest, ...]; root is the union
        req = getattr(responder, "request", None)
        root = getattr(req, "root", None) if req else None
        if isinstance(root, types.InitializeRequest):
            # Store synchronously before original handler responds, so tools see it immediately
            set_client_capabilities(root.params)  # type: ignore[arg-type]
            logging.info(f"Intercepted client capabilities on initialize: {root.params.capabilities}")
    except Exception as e:
        logging.debug(f"Capability intercept failed (non-fatal): {e}")
    return await _original_received_request(self, responder)

ServerSession._received_request = _patched_received_request  # type: ignore[method-assign]

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