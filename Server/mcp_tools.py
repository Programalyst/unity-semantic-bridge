from unity_bridge import forward_to_unity
from typing import Annotated

def register_unity_tools(mcp):
    """Registers all Unity-specific tools to the provided MCP instance."""

    @mcp.tool()
    async def get_scene_hierarchy(
        depth: Annotated[int, "How many levels deep to traverse. Use 2 for a quick overview, 3–5 to find deeply nested objects. "] = 2,
        includeLayers: Annotated[bool, "If true, includes the layer (e.g. 'Default', 'UI') for each object. Omit if not needed to reduce output size."] = True,
        includeComponents: Annotated[bool, "If true, includes component names on each GameObject (e.g. 'Rigidbody', 'UnitHealth'). Required if you need to know what components exist before calling get_component_inspector_values."] = True,
        includePosition: Annotated[bool, "If true, includes world-space position for each object. Omit if not needed to reduce output size."] = True,
    ) -> str:
        """
        Returns the current Unity scene hierarchy as a list of GameObjects with their paths and instance_ids.
    
        This is usually the FIRST tool to call — use it to discover GameObjects and their instance_ids,
        which are required by inspect_gameobject and get_component_inspector_values.
        
        Tip: set includeComponents=True to confirm a component exists on a GameObject before inspecting it.
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
        instance_id: Annotated[int, "Get the instance_id from the 'get_scene_hierarchy' tool output."]
    ) -> str:
        """
        Detailed inspection of a GameObject including components and public fields.
        """
        return await forward_to_unity({
            "action": "Inspect_GameObject",
            "instanceID": instance_id
        })
    
    @mcp.tool()
    async def get_component_inspector_values(
        instance_id: Annotated[int, "The instance_id of the GameObject. Obtain this from get_scene_hierarchy."],
        component_name: Annotated[str, "The exact component class name to inspect (e.g. 'UnitHealth', 'Rigidbody'). Use get_scene_hierarchy with includeComponents=True to find valid component names."]
    ) -> str:
        """
        Retrieves all serialized field values currently visible in the Unity Inspector for a specific component on a GameObject.
        This includes [SerializeField] private fields and prefab overrides — i.e. live Editor values that may differ from source code defaults.
        
        Typical workflow:
        1. Call get_scene_hierarchy (with includeComponents=True) to find the GameObject's instance_id and confirm the component name.
        2. Call this tool with that instance_id and component_name.
        
        To read the component's source logic instead of its values, use get_component_code.
        """
        return await forward_to_unity({
            "action": "Get_InspectorValues",
            "instanceID": instance_id,
            "componentName": component_name
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
    
    @mcp.tool()
    async def add_component(
        instance_id: Annotated[int, "The instance_id of the target GameObject. Get this from 'get_scene_hierarchy' or 'inspect_gameobject'."],
        component_type: Annotated[str, "Fully-qualified C# type name of the component to add. E.g. 'UnityEngine.Rigidbody', 'UnityEngine.CapsuleCollider', or a custom type like 'MyNamespace.UnitLimb'."],
        allow_duplicate: Annotated[bool, "If False (default), skips adding if a component of this type already exists and returns the existing component's instance_id. If True, adds a second instance regardless."] = False
    ) -> str:
        """
        Adds a component to a GameObject by instance_id.
        If the component already exists, behaviour is controlled by allow_duplicate.
        Returns the new (or existing) component's instance_id and the added type name.
        """
        return await forward_to_unity({
            "action": "Add_Component",
            "instanceID": instance_id,
            "componentType": component_type,
            "allowDuplicate": allow_duplicate
        })


    @mcp.tool()
    async def set_field_value(
        instance_id: Annotated[int, "The instance_id of the GameObject that owns the component. Get this from 'get_scene_hierarchy' or 'inspect_gameobject'."],
        component_name: Annotated[str, "Name of the component type whose fields will be set. E.g. 'Rigidbody', 'CapsuleCollider', 'UnitLimb'. Use 'get_component_inspector_values' to see available field names."],
        fields: Annotated[dict, "Key-value map of field names to new values. Values may be: primitives (float, int, bool, string), structs as dicts (e.g. {'x':0,'y':1,'z':0}), enum strings, or integer instance_ids for Object reference fields."],
        component_index: Annotated[int, "Zero-based index to disambiguate when multiple components of the same type exist on the GameObject. Defaults to 0 (first found)."] = 0
    ) -> str:
        """
        Sets one or more serialized field values on a component attached to a GameObject.
        Records an Undo operation so changes are reversible in the Editor.
        Use 'get_component_inspector_values' first to discover field names and current values.
        """
        return await forward_to_unity({
            "action": "Set_FieldValue",
            "instanceID": instance_id,
            "componentName": component_name,
            "fields": fields,
            "componentIndex": component_index
        })
    
    
    @mcp.tool()
    async def get_lights_affecting_object(
        instance_id: Annotated[int, "The instance_id of the GameObject. Obtain this from get_scene_hierarchy."],
    ) -> str:
        """
        Returns:
        - All Light components in the scene
        - For each light: name, type, intensity, range, position, lightmapping mode, culling mask, rendering layer mask
        - Distance from each light to the target object
        - Whether the target is within range of each light
        - Total count of lights in range (to spot per-object limit issues)
        """
        return await forward_to_unity({
            "action": "Get_LightsAffectingObject",
            "instanceID": instance_id
        })
    
    @mcp.tool()
    async def get_urp_pipeline_settings() -> str:
        """
        Returns the URP render pipeline asset's current render path setting
        """
        return await forward_to_unity({
            "action": "Get_UrpPipelineSettings"
        })