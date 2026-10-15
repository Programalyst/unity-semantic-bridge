import asyncio
import logging
import os
import sys
from fastmcp import FastMCP

from mcp_tools import register_unity_tools
from events import event_server, registration
from events.event_handlers import register_event_handlers
from state_manager import app_state, resolve_ports

from pathlib import Path
from dotenv import load_dotenv
load_dotenv(dotenv_path=Path(__file__).resolve().parent / ".env", override=False)  # mcp-editor-bridge/.env (see .env.example); explicit path so uv --directory works regardless of CWD

# --- CONFIG & STATE ---
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')

mcp = FastMCP(
    "UnitySemanticBridge",
    instructions=(
        "Unity throttles its Editor main thread whenever its window is unfocused, "
        "which stalls Play Mode transitions, domain reloads, and asset refreshes for minutes. "
        "Unless the user prefers otherwise, always start any session that drives the Editor "
        "(enter_play/exit_play, refresh_assets, write_unity_script, invoke_menu_item, invoke_static) "
        "with set_editor_throttling(\"no_throttling\"), and restore the previous mode with "
        "set_editor_throttling(\"restore\") when done."
    ),
)
register_unity_tools(mcp)
register_event_handlers() # must run/register before event_server.start_event_server()

async def run_servers():
    # Capture the main event loop so async event handlers can schedule work
    # onto it via run_coroutine_threadsafe
    # makes it possible for events recieved to be put on a shared queue/state
    event_server.set_main_loop(asyncio.get_running_loop())

    # Unity hosts the HTTP server at 127.0.0.1:<unity port>/rpc (JSON-RPC 2.0).
    # Python also hosts a JSON-RPC event server at 127.0.0.1:<event port>/rpc so Unity
    # can push notifications (hierarchy/selection/play-mode changes etc.).
    event_server.start_event_server()
    logging.info(f"🚀 UnitySemanticBridge MCP server starting (Unity RPC at {app_state.unity_rpc_url}, Python events at {event_server.bound_url()})...")
    # Registration lets several servers (agent sessions) share one Editor's events.
    renewals = asyncio.create_task(registration.registration_loop())
    await mcp.run_stdio_async()
    renewals.cancel()
    logging.info("🔌 MCP Server stopped.")
    event_server.stop_event_server()

if __name__ == "__main__":
    try:
        app_state.configure_ports(*resolve_ports(sys.argv[1:], os.environ))
    except ValueError as e:
        sys.exit(f"mcp-editor-bridge: {e}")
    asyncio.run(run_servers())
