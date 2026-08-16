import asyncio
import logging
from mcp.server.fastmcp import FastMCP
from mcp.server.session import ServerSession
from mcp import types

from mcp_tools import register_unity_tools
from capability_gate import set_client_capabilities

from dotenv import load_dotenv
load_dotenv()  # Load environment variables from .env file

# --- CONFIG & STATE ---
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
mcp = FastMCP("UnitySemanticBridge")
register_unity_tools(mcp)

# --- UPFRONT FEATURE-GATING: intercept client capabilities on initialize ---
_original_received_request = ServerSession._received_request

async def _patched_received_request(self, responder):  # type: ignore[no-untyped-def]
    try:
        req = getattr(responder, "request", None)
        root = getattr(req, "root", None) if req else None
        if isinstance(root, types.InitializeRequest):
            set_client_capabilities(root.params)  # type: ignore[arg-type]
            logging.info(f"Intercepted client capabilities on initialize: {root.params.capabilities}")
    except Exception as e:
        logging.debug(f"Capability intercept failed (non-fatal): {e}")
    return await _original_received_request(self, responder)

ServerSession._received_request = _patched_received_request  # type: ignore[method-assign]


async def run_servers():
    # Unity now hosts the HTTP server (127.0.0.1:1073); the MCP server is purely
    # a stdio MCP process that POSTs to Unity via unity_bridge.send_to_unity().
    logging.info("🚀 UnitySemanticBridge MCP server starting (HTTP bridge to Unity at 127.0.0.1:1073)...")
    await mcp.run_stdio_async()
    logging.info("🔌 MCP Server stopped.")

if __name__ == "__main__":
    asyncio.run(run_servers())
