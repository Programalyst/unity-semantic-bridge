from unity_bridge import forward_to_unity
from typing import Annotated

def register_unity_tools(mcp):
    """Registers all Unity-specific tools to the provided MCP instance."""

    @mcp.tool()
    async def get_unity_hierarchy(
        depth: Annotated[int, "How many levels deep to traverse. Default is 2."] = 2,
        includeLayers: Annotated[bool, "If true, includes layers for each object."] = True,
        includeComponents: Annotated[bool, "If true, includes component names."] = True,
        includePosition: Annotated[bool, "If true, includes object position."] = True,
    ) -> str:
        """
        Requests the current Unity scene hierarchy. 
        Use 'minimal=True' for a fast overview of the scene structure.
        """
        return await forward_to_unity({
            "action": "Get_SceneHierarchy",
            "depth": depth,
            "includeLayers": includeLayers,
            "includeComponents": includeComponents,
            "includePosition": includePosition
        })
    
    @mcp.tool()
    async def notify_unity(text: str) -> str:
        """Sends a message to the Unity Editor chat window."""
        return await forward_to_unity({
            "action": "Notify_Unity",
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
            "action": "Search_Assets",
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
            "action": "Get_FolderStructure", 
            "path": folder_path
        })

    @mcp.tool()
    async def find_asset_references(
        asset_path: Annotated[str, "The full project-relative path to the asset, including extension (e.g., 'Assets/Prefabs/Player.prefab')"]
    ) -> str:
        """Finds all assets or scenes that reference a specific asset path."""
        return await forward_to_unity({
            "action": "Find_AssetReferences", 
            "path": asset_path
        })
    
    @mcp.tool()
    async def write_unity_script(
        path: Annotated[str, "Path should be relative to Assets/ (e.g., 'Assets/Scripts/MyNewSensor.cs')."], 
        content: str
    ) -> str:
        """
        Writes or overwrites a C# script in the Unity project.
        Automatically triggers Unity recompilation.
        """
        return await forward_to_unity({
            "action": "WRITE_SCRIPT",
            "path": path,
            "content": content
        })
    
    @mcp.tool()
    async def get_unity_console_logs() -> str:
        """Returns the most recent errors and warnings from the Unity Console."""
        return await forward_to_unity({"action": "GET_CONSOLE_LOGS"})
    
    @mcp.tool()
    async def set_unity_play_mode(enabled: bool) -> str:
        """Enters or exits Play Mode in the Unity Editor."""
        return await forward_to_unity({"action": "SET_PLAY_MODE", "enabled": enabled})
    
    @mcp.tool()
    async def clear_unity_console_logs() -> str:
        """Clears old Unity Editor console logs."""
        return await forward_to_unity({"action": "CLEAR_CONSOLE_LOGS"})
    
    @mcp.tool()
    async def inspect_gameobject(
        instance_id: Annotated[int, "Get the instance_id from the 'get_unity_hierarchy' tool output."]
    ) -> str:
        """
        Detailed inspection of a GameObject including components and public fields.
        """
        return await forward_to_unity({
            "action": "Inspect_GameObject",
            "instanceID": instance_id
        })
    
    @mcp.tool()
    async def get_component_code(
        component_name: Annotated[str, "The exact name of the C# class/component (e.g., 'HealthHandler' or 'Projectile')."]
    ) -> str:
        """
        Locates and returns the full C# source code for a specific Unity component.
        Use this to analyze the logic of scripts identified via 'inspect_gameobject'.
        """
        return await forward_to_unity({
            "action": "Get_ComponentCode",
            "componentName": component_name
        })

    @mcp.tool()
    async def get_unity_physics_layers() -> str:
        """
        Returns the Unity Physics Collision Matrix.
        Shows which layers are configured to collide with each other or ignore each other.
        Essential for diagnosing 'friend or foe' collision or trigger issues.
        """
        return await forward_to_unity({
            "action": "Get_PhysicsMatrix"
        })