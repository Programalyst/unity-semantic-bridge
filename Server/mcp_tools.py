from unity_bridge import forward_to_unity, fetch_screenshot_base64
from typing import Annotated
from LightingAgent.lighting_agent import LightingDiagnosticAgent
from mcp.server.fastmcp import Image
import base64

# Global reference to lighting agent (initialized after tools are registered)
_lighting_agent = None

def register_unity_tools(mcp):
    """Registers all Unity-specific tools to the provided MCP instance."""

    @mcp.tool()
    async def get_screenshot() -> Image:
        """
        Captures a screenshot of the current Unity Game view and returns it as an image.
        Useful for visually inspecting lighting, UI layout, or general scene appearance.
        """
        b64 = await fetch_screenshot_base64()
        return Image(data=base64.b64decode(b64), format="jpeg")
    
    @mcp.tool()
    async def get_scene_hierarchy(
        depth: Annotated[int, "How many levels deep to traverse. Use 2 for a quick overview, 3–5 to find deeply nested objects. "] = 2,
        max_nodes: Annotated[int, "Maximum nodes to return before truncating, to avoid overwhelming context on large scenes."] = 300,
        include_layers: Annotated[bool, "If true, includes the layer (e.g. 'Default', 'UI') for each object. Omit if not needed to reduce output size."] = True,
        include_components: Annotated[bool, "If true, includes component names on each GameObject (e.g. 'Rigidbody', 'UnitHealth'). Required if you need to know what components exist before calling get_component_inspector_values."] = True,
        include_positions: Annotated[bool, "If true, includes world-space position for each object. Omit if not needed to reduce output size."] = True,
        only_main_cam_visible: Annotated[bool, "If true, objects out of the main camera view will be culled."] = False
    ) -> str:
        """
        Returns the current Unity scene hierarchy as a list of GameObjects with their paths and instance_ids.
        Note: Scene hierarchy subtree traversal will always be pruned if a SkinnedMeshRenderer is encountered. Use get_gameobject_tree to inspect a character's rig
    
        This is usually the FIRST tool to call — use it to discover GameObjects and their instance_ids,
        which are required by inspect_gameobject and get_component_inspector_values.
        
        Tip: set includeComponents=True to confirm a component exists on a GameObject before inspecting it.
        """
        return await forward_to_unity({
            "action": "Get_SceneHierarchy",
            "depth": depth,
            "maxNodes": max_nodes,
            "includeLayers": include_layers,
            "includeComponents": include_components,
            "includePositions": include_positions,
            "onlyMainCamVisible": only_main_cam_visible
        })
    
    @mcp.tool()
    async def get_gameobject_tree(
        instance_id: Annotated[int, "The instance_id of the root GameObject to traverse. Get this from 'get_scene_hierarchy'."],
        depth: Annotated[int, "How many levels of children to include. Default is 5."] = 5,
        include_components: Annotated[bool, "Whether to include component names on each GameObject. Default is True."] = True,
        include_positions: Annotated[bool, "Whether to include world position of each GameObject. Default is False."] = False,
    ) -> str:
        """
        Returns the child hierarchy of a single GameObject as a flat list.
        Use this instead of get_scene_hierarchy when you already know the root object
        and want to avoid fetching the entire scene (faster, less likely to time out).
        """
        return await forward_to_unity({
            "action": "Get_GameObjectTree",
            "instanceID": instance_id,
            "depth": depth,
            "includeComponents": include_components,
            "includePositions": include_positions,
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
        search_in_folders: Annotated[list[str], "List of folder paths to search, e.g. ['Assets/Scripts']"] = ["Assets"]
    ) -> str:
        """Finds assets in Unity. Default folders: ['Assets']."""
        return await forward_to_unity({
            "action": "Search_Assets",
            "filter": filter_query,
            "limit": limit,
            "searchInFolders": search_in_folders
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

    @mcp.tool()
    async def diagnose_lighting_issue(
        instance_id: Annotated[int, "The instance_id of the GameObject with lighting issues. Get this from 'get_scene_hierarchy'."],
        issue_description: Annotated[str, "Description of the lighting problem (e.g., 'object appears too dark', 'shadows not rendering', 'lights not affecting object')"],
        max_iterations: Annotated[int, "Maximum diagnostic iterations before stopping (default 10)"] = 10
    ) -> str:
        """
        Launches an autonomous diagnostic agent that will iteratively investigate and diagnose lighting issues on a GameObject.

        The agent will:
        1. Check lights affecting the object
        2. Inspect URP pipeline settings
        3. Examine renderer components and materials
        4. Keep iterating until the issue is identified or max iterations reached

        Use this when you need deep, multistep lighting diagnostics rather than manual tool calls.
        Returns a comprehensive diagnostic report with findings and recommendations.
        """
        global _lighting_agent

        # Initialize agent with Unity tools on first use
        if _lighting_agent is None:
            unity_tools = {
                "get_lights_affecting_object": get_lights_affecting_object,
                "get_urp_pipeline_settings": get_urp_pipeline_settings,
                "get_component_inspector_values": get_component_inspector_values,
                "inspect_gameobject": inspect_gameobject,
            }
            _lighting_agent = LightingDiagnosticAgent(unity_tools, max_iterations=max_iterations)

        return await _lighting_agent.diagnose_lighting_issue(instance_id, issue_description)