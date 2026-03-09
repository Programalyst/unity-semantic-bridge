from unity_bridge import forward_to_unity
from typing import Annotated

def register_unity_tools(mcp):
    """Registers all Unity-specific tools to the provided MCP instance."""

    @mcp.tool()
    async def get_unity_hierarchy() -> str:
        """Requests the current Unity scene hierarchy and component data."""
        return await forward_to_unity({"action": "MCP_GET_SCENE"})
    
    @mcp.tool()
    async def notify_unity(text: str) -> str:
        """Sends a message to the Unity Editor chat window."""
        return await forward_to_unity({
            "action": "MCP_NOTIFY",
            "message": f"IDE Agent: {text}",
        })

    @mcp.tool()
    async def find_unity_files(
        filter_query: Annotated[str, "The search string (e.g., 't:Prefab Player' or 'l:LabelName')"], 
        limit: Annotated[int, "Max results to return (default 10)"] = 10, 
        searchInFolders: Annotated[list[str], "List of folder paths to search, e.g. ['Assets/Scripts']"] = ["Assets"]
    ) -> str:
        """Finds assets in Unity. Default folders: ['Assets']."""
        return await forward_to_unity({
            "action": "MCP_SEARCH_LIMIT",
            "filter": filter_query,
            "limit": limit,
            "searchInFolders": searchInFolders
        })

    @mcp.tool()
    async def get_project_tree(
        folder_path: Annotated[str, "The project-relative path (e.g., 'Assets/Scripts') to start the tree from"] = "Assets"
    ) -> str:
        """Returns the folder structure starting from the given path."""
        return await forward_to_unity({
            "action": "MCP_TREE", 
            "path": folder_path
        })

    @mcp.tool()
    async def find_asset_references(
        asset_path: Annotated[str, "The full project-relative path to the asset, including extension (e.g., 'Assets/Prefabs/Player.prefab')"]
    ) -> str:
        """Finds all assets or scenes that reference a specific asset path."""
        return await forward_to_unity({
            "action": "MCP_GREP", 
            "path": asset_path
        })