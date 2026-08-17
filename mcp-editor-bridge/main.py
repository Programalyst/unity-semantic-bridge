import asyncio
import logging
from fastmcp import FastMCP

from mcp_tools import register_unity_tools

from dotenv import load_dotenv
load_dotenv()  # Load environment variables from .env file

# --- CONFIG & STATE ---
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
mcp = FastMCP("UnitySemanticBridge")
register_unity_tools(mcp)


async def run_servers():
    # Unity now hosts the HTTP server (127.0.0.1:1073); the MCP server is purely
    # a stdio MCP process that POSTs to Unity via unity_bridge.send_to_unity().
    logging.info("🚀 UnitySemanticBridge MCP server starting (HTTP bridge to Unity at 127.0.0.1:1073)...")
    await mcp.run_stdio_async()
    logging.info("🔌 MCP Server stopped.")

if __name__ == "__main__":
    asyncio.run(run_servers())
