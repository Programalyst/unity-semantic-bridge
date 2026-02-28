import asyncio
import json
import websockets
from mcp.server.fastmcp import FastMCP

# Initialize the MCP Sub-Agent
mcp = FastMCP("UnitySceneSubAgent")

BRIDGE_URI = "ws://127.0.0.1:8765" # Connects to main.py handshake or a dedicated port

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
            request = {"content": "MCP_REQUEST_SCENE"}
            await ws.send(json.dumps(request))
            
            # 3. Wait for the response (The JSON Scene)
            response = await ws.recv()
            data = json.loads(response)
            return json.dumps(data.get("scene", {}), indent=2)
            
    except Exception as e:
        return f"Failed to reach Unity Bridge: {e}. Ensure main.py is running."

@mcp.tool()
async def notify_unity(text: str) -> str:
    """Sends a message to the Unity Editor chat window."""
    async with websockets.connect(BRIDGE_URI) as ws:
        await ws.send(json.dumps({"content": f"IDE Agent: {text}"}))
        response = await ws.recv() 
        data = json.loads(response)
        return data.get("content", "Notification sent.")

@mcp.tool()
async def search_unity_assets(filter_query: str) -> str:
    """
    Unity equivalent of 'glob'. Searches for assets by type, name, or label.
    Example filters: 't:Prefab Player', 't:Script health', 'l:UI_Assets'
    """
    async with websockets.connect(BRIDGE_URI) as ws:
        # Forwarding the glob-style filter to Unity's AssetDatabase.FindAssets
        await ws.send(json.dumps({"content": f"MCP_GLOB:{filter_query}"}))
        response = await ws.recv()
        return response

@mcp.tool()
async def get_project_tree(folder_path: str = "Assets") -> str:
    """
    Unity equivalent of 'tree'. Returns the folder structure starting from the given path.
    Use this to see where scripts, prefabs, and materials are stored.
    """
    async with websockets.connect(BRIDGE_URI) as ws:
        await ws.send(json.dumps({"content": f"MCP_TREE:{folder_path}"}))
        response = await ws.recv()
        return response

@mcp.tool()
async def find_asset_references(asset_path: str) -> str:
    """
    Unity equivalent of 'grep'. Finds all objects or scenes that reference a specific asset.
    Useful for seeing which Prefabs use a specific Script.
    """
    async with websockets.connect(BRIDGE_URI) as ws:
        await ws.send(json.dumps({"content": f"MCP_GREP:{asset_path}"}))
        response = await ws.recv()
        return response


@mcp.tool()
async def find_unity_files(filter_query: str, limit: int = 5) -> str:
    """
    Unity equivent of 'find'. Finds assets and immediately returns their file paths.
    'limit' acts like 'head' to keep context clean.
    Filter examples: 't:Prefab', 't:Script Player', 'l:Gold_Master'
    Returns a JSON list of paths like ["Assets/Scripts/Player.cs", ...]
    """
    async with websockets.connect(BRIDGE_URI) as ws:
        # We send a specific combined request type
        await ws.send(json.dumps({"content": f"MCP_SEARCH_LIMIT:{limit}|{filter_query}"}))
        
        response = await ws.recv()
        return response


if __name__ == "__main__":
    mcp.run()