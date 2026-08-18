import asyncio
import logging
from fastmcp import FastMCP

from mcp_tools import register_unity_tools
import event_server
from state_manager import app_state

from dotenv import load_dotenv
load_dotenv()  # Load environment variables from .env file

# --- CONFIG & STATE ---
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
mcp = FastMCP("UnitySemanticBridge")
register_unity_tools(mcp)

# Default event handlers — users can add more via event_server.register_handler.
# These simply log; replace with custom logic if needed.
@event_server.on_event("unity/hierarchyChanged")
def _on_hierarchy_changed(params):
    logging.info(f"[event] hierarchyChanged: {params}")
    return "ack"

@event_server.on_event("unity/selectionChanged")
def _on_selection_changed(params):
    logging.info(f"[event] selectionChanged: {params}")
    return "ack"

@event_server.on_event("unity/playModeStateChanged")
def _on_playmode_changed(params):
    logging.info(f"[event] playModeStateChanged: {params}")
    return "ack"

@event_server.on_event("unity/consoleLog")
def _on_console_log(params):
    logging.info(f"[event] consoleLog: {params}")
    return "ack"

@event_server.on_event("unity/ping")
def _on_ping(params):
    return {"pong": True, "echo": params}


async def run_servers():
    # Unity hosts the HTTP server at 127.0.0.1:1073/rpc (JSON-RPC 2.0).
    # Python also hosts a JSON-RPC event server at 127.0.0.1:1074/rpc so Unity
    # can push notifications (hierarchy/selection/play-mode changes etc.).
    event_server.start_event_server()
    logging.info(f"🚀 UnitySemanticBridge MCP server starting (Unity RPC at {app_state.unity_rpc_url}, Python events at {app_state.python_event_url})...")
    await mcp.run_stdio_async()
    logging.info("🔌 MCP Server stopped.")
    event_server.stop_event_server()

if __name__ == "__main__":
    asyncio.run(run_servers())
