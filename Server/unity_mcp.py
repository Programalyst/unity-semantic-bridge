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
        # 1. Connect to Main Bridge
        async with websockets.connect(BRIDGE_URI) as ws:
            # 2. Send a request that main.py will forward to Unity
            payload = {
                "action": "MCP_GET_SCENE",
            }
            await ws.send(json.dumps(payload))
            
            # 3. Wait for the response (The JSON Scene)
            response = await ws.recv()
            return response
            
    except Exception as e:
        return f"Failed to reach Unity Bridge: {e}. Ensure main.py is running."

@mcp.tool()
async def notify_unity(text: str) -> str:
    """Sends a message to the Unity Editor chat window."""
    async with websockets.connect(BRIDGE_URI) as ws:
        payload = {
            "action": "MCP_NOTIFY",
            "message": f"IDE Agent: {text}",
        }
        await ws.send(json.dumps(payload))
        response = await ws.recv() 
        return response

@mcp.tool()
async def search_unity_assets(filter_query: str, limit: int = 10, searchInFolders: list[str] = ["Assets"]) -> str:
    """
    Unity equivalent of 'glob'. Searches for assets by type, name, or label.
    Uses UnityEditor.AssetDatabase.FindAssets(filter, searchInFolders)
    Example filters: 
    By type: 't:Prefab Player', 't:Script health'
    By label: 'l:UI_Assets'
    'limit' acts like 'head' to set a limit on search results
    'folders' is a list of paths to search in (e.g. ["Assets/Scripts", "Assets/Prefabs"]).
    Defaults to ["Assets"] to exclude Packages.
    """
    async with websockets.connect(BRIDGE_URI) as ws:
        payload = {
            "action": "MCP_GLOB",
            "filter": filter_query,
            "limit": limit,
            "searchInFolders": searchInFolders
        }
        await ws.send(json.dumps(payload))
        response = await ws.recv()
        return response

@mcp.tool()
async def get_project_tree(folder_path: str = "Assets") -> str:
    """
    Unity equivalent of 'tree'. Returns the folder structure starting from the given path.
    Use this to see where scripts, prefabs, and materials are stored.
    """
    async with websockets.connect(BRIDGE_URI) as ws:
        payload = {
            "action": "MCP_TREE",
            "content": folder_path
        }
        await ws.send(json.dumps(payload))
        response = await ws.recv()
        return response

@mcp.tool()
async def find_asset_references(asset_path: str) -> str:
    """
    Unity equivalent of 'grep'. Finds all objects or scenes that reference a specific asset.
    Useful for seeing which Prefabs use a specific Script.
    """
    async with websockets.connect(BRIDGE_URI) as ws:
        payload = {
            "action": "MCP_GREP",
            "asset_path": asset_path,
        }
        await ws.send(json.dumps(payload))
        response = await ws.recv()
        return response

if __name__ == "__main__":
    mcp.run()