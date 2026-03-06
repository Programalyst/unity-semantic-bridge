import json
import websockets
from mcp.server.fastmcp import FastMCP
from typing import Annotated

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
async def search_unity_assets(
    filter_query: Annotated[str, "The search string (e.g., 't:Prefab Player' or 'l:LabelName')"], 
    limit: Annotated[int, "Max results to return (default 10)"] = 10, 
    searchInFolders: Annotated[list[str], "List of folder paths to search, e.g. ['Assets/Scripts']"] = ["Assets"]
    ) -> str:
    """
    Searches for Unity assets using AssetDatabase.FindAssets. 
    Use this to find specific prefabs, scripts, or assets by type and label.
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
async def get_project_tree(
    folder_path: Annotated[str, "The project-relative path (e.g., 'Assets/Scripts') to start the tree from"] = "Assets"
    ) -> str:
    """
    Returns a recursive visual folder structure. 
    Use this first to orient yourself within the project's directory layout before searching for specific files.
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
async def find_asset_references(
    asset_path: Annotated[str, "The full project-relative path to the asset, including extension (e.g., 'Assets/Prefabs/Player.prefab')"]
    ) -> str:
    """
    Finds all scenes, prefabs, and assets that depend on or reference the specified asset.
    Essential for impact analysis before deleting an asset or changing a script's public variables.
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