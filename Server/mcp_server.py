import asyncio
import json
import websockets
from mcp.server.fastmcp import FastMCP

# Initialize the MCP Sub-Agent
mcp = FastMCP("UnitySceneSubAgent")

BRIDGE_URI = "ws://127.0.0.1:8765" # Connects to your main.py handshake or a dedicated port

@mcp.tool()
async def get_unity_hierarchy() -> str:
    """
    Requests the current Unity scene hierarchy and component data.
    Use this to understand the project structure before writing code.
    """
    try:
        # 1. Connect to your Main Bridge
        async with websockets.connect(BRIDGE_URI) as ws:
            # 2. Send a request that main.py will forward to Unity
            request = {"type": "chat", "message": "MCP_REQUEST_SCENE"}
            await ws.send(json.dumps(request))
            
            # 3. Wait for the response (The JSON Scene)
            response = await ws.recv()
            data = json.loads(response)
            return json.dumps(data.get("scene", {}), indent=2)
            
    except Exception as e:
        return f"Failed to reach Unity Bridge: {e}. Ensure main.py is running."

@mcp.tool()
async def notify_unity_developer(text: str) -> str:
    """Sends a message to the Unity Editor chat window."""
    async with websockets.connect(BRIDGE_URI) as ws:
        await ws.send(json.dumps({"type": "chat", "message": f"IDE Agent: {text}"}))
        return "Notification sent."

if __name__ == "__main__":
    mcp.run()