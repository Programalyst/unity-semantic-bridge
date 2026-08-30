from unity_bridge import call_unity
from typing import Annotated, Literal
from LightingAgent.lighting_agent import LightingDiagnosticAgent
from fastmcp.utilities.types import Image
import base64
import json
import time
from events import events_buffer

# Global reference to lighting agent (initialized after tools are registered)
_lighting_agent = None

def register_unity_tools(mcp):
    """Registers all Unity-specific tools to the provided MCP instance."""

    @mcp.tool()
    async def get_project_settings(
        sections: Annotated[list[str] | None, "Which sections to return. Valid: 'core', 'rendering', 'input', 'ui', 'scripting', 'tags_layers'. Omit or pass empty for all."] = None,
    ) -> str:
        """
        Returns Unity Project Settings as JSON. Selectively fetch sections to keep output small.

        Sections:
        - core: Unity version, editor platform, active build target/group, company/product/bundleVersion
        - rendering: active pipeline (Built-in/URP/HDRP), URP asset path, color space, MSAA/quality, plus URP details (folded from get_urp_pipeline_settings)
        - input: Active Input Handling (Old/New/Both) and default InputActionAsset assets if any
        - ui: uGUI vs UI Toolkit signals — EventSystem/Canvas in open scenes, UIDocument/VisualTreeAsset/StyleSheet counts
        - scripting: API compatibility level, #define symbols (group), allowUnsafeCode, scripting backend
        - tags_layers: Tag list and layer names/index map (physics collision matrix stays in get_physics_layers)

        Example: get_project_settings(sections=["core","rendering"]) for a focused query.
        """
        params: dict = {}
        if sections is not None:
            # Normalize to lower-case, handle tags alias
            normed = []
            for s in sections:
                t = s.strip().lower()
                if t in ("tags", "tagslayers"):
                    t = "tags_layers"
                normed.append(t)
            params["sections"] = normed
        return await call_unity("get_project_settings", params)

    @mcp.tool()
    async def get_screenshot(
        source: Annotated[Literal["game", "scene"], "'game': renders from Camera.main — what the player sees in Play mode or a build. 'scene': renders from the Editor's Scene view camera — wherever it's currently aimed by the user."] = "game",
        focus_instance_id: Annotated[int | None, "Scene view only. Frame the Scene camera on this GameObject before capturing, instead of using its current position. Ignored when source='game'."] = None,
        jpg_quality: Annotated[int, "JPEG quality, 1-100."] = 50,
        max_width: Annotated[int, "Max image width in pixels; height scales to preserve aspect ratio."] = 1280,
    ) -> Image:
        """
        Captures a screenshot and returns it as an image. Useful for visually inspecting lighting, UI layout, or general scene appearance.
        """
        params: dict = {
            "source": source,
            "jpgQuality": jpg_quality,
            "maxWidth": max_width,
        }
        if focus_instance_id is not None:
            params["focusInstanceId"] = focus_instance_id
        result = await call_unity("get_screenshot", params)
        if result.startswith("Error:"):
            raise RuntimeError(result)
        return Image(data=base64.b64decode(result), format="jpeg")
    
    @mcp.tool()
    async def get_scene_hierarchy(
        depth: Annotated[int, "How many levels deep to traverse. Use 2 for a quick overview, 3–5 to find deeply nested objects. "] = 2,
        max_nodes: Annotated[int, "Maximum nodes to return before truncating, to avoid overwhelming context on large scenes."] = 300,
        include_layers: Annotated[bool, "If true, includes the layer (e.g. 'Default', 'UI') for each object. Omit if not needed to reduce output size."] = True,
        include_components: Annotated[bool, "If true, includes component names on each GameObject (e.g. 'Rigidbody', 'UnitHealth'). Required if you need to know what components exist before calling get_component_inspector_values."] = True,
        include_positions: Annotated[bool, "If true, includes world-space position for each object. Omit if not needed to reduce output size."] = True,
        only_main_cam_visible: Annotated[bool, "If true, objects out of the main camera view will be culled."] = False,
        root_instance_id: Annotated[int | None, "Optional instance_id to start traversal from instead of the scene root — e.g. a UI Canvas root, or any subtree found via a previous call. depth/max_nodes apply relative to this root. Get the id from a prior get_scene_hierarchy or get_gameobject_tree call."] = None
    ) -> str:
        """
        Returns the current Unity scene hierarchy as a list of GameObjects with their paths and instance_ids.
        Note: Scene hierarchy subtree traversal will always be pruned if a SkinnedMeshRenderer is encountered. Use get_gameobject_tree to inspect a character's rig
    
        This is usually the FIRST tool to call — use it to discover GameObjects and their instance_ids, which are required by inspect_gameobject and get_component_inspector_values.
        
        Tip: set includeComponents=True to confirm a component exists on a GameObject before inspecting it.
        Tip: for a specific known subtree (e.g. a UI Canvas), pass its instance_id as root_instance_id instead of raising max_nodes to reach it from the scene root.
        """
        return await call_unity("get_scene_hierarchy", {
            "depth": depth,
            "maxNodes": max_nodes,
            "includeLayers": include_layers,
            "includeComponents": include_components,
            "includePositions": include_positions,
            "onlyMainCamVisible": only_main_cam_visible,
            "rootInstanceId": root_instance_id
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
        return await call_unity("get_gameobject_tree", {
            "instanceId": instance_id,
            "depth": depth,
            "includeComponents": include_components,
            "includePositions": include_positions,
        })
    
    @mcp.tool()
    async def notify_unity(text: str) -> str:
        """Sends a message to the Unity Editor chat window."""
        return await call_unity("notify_unity", {
            "message": f"IDE Agent: {text}",
        })

    @mcp.tool()
    async def find_unity_files(
        filter: Annotated[str, "The search string (e.g., 't:Prefab Player' or 'l:LabelName')"], 
        limit: Annotated[int, "Max results to return (default 10)"] = 10, 
        folders: Annotated[list[str], "List of folder paths to search, e.g. ['Assets/Scripts']"] = ["Assets"]
    ) -> str:
        """Finds assets in Unity. Default folders: ['Assets']."""
        return await call_unity("find_unity_files", {
            "filter": filter,
            "limit": limit,
            "folders": folders
        })

    @mcp.tool()
    async def get_project_tree(
        folder_path: Annotated[str, "The project-relative path (e.g., 'Assets/Scripts') to start the tree from"] = "Assets"
    ) -> str:
        """Returns the folder structure starting from the given path."""
        return await call_unity("get_project_tree", {
            "path": folder_path
        })

    @mcp.tool()
    async def find_asset_references(
        asset_path: Annotated[str, "The full project-relative path to the asset, including extension (e.g., 'Assets/Prefabs/Player.prefab')"]
    ) -> str:
        """Finds all assets or scenes that reference a specific asset path."""
        return await call_unity("find_asset_references", {
            "path": asset_path
        })
    
    @mcp.tool()
    async def delete_asset(
        path: Annotated[str, "Project-relative path to delete, e.g. 'Assets/DataScriptableObjects/Unit Visual - Test.asset'. Must be under Assets/."],
    ) -> str:
        """Deletes an asset via AssetDatabase.DeleteAsset (supports Undo)."""
        return await call_unity("delete_asset", {"path": path})

    @mcp.tool()
    async def write_unity_script(
        path: Annotated[str, "Path should be relative to Assets/ (e.g., 'Assets/Scripts/MyNewSensor.cs')."], 
        content: Annotated[str, "Full C# source to write. Overwrites the entire file if it already exists."],
        confirm: Annotated[bool, "Required to overwrite an existing file. Leave false on the first attempt: if the file already exists, the call returns CONFIRM_REQUIRED along with its current contents instead of writing, so you can review before retrying with confirm=true. Not needed when creating a new file."] = False
    ) -> str:
        """
        Writes or overwrites a C# script in the Unity project and triggers recompilation.

        Returns one of:
        - CONFIRM_REQUIRED: ... — the file already exists; nothing was written. Review the returned
        contents, then re-call with confirm=true if you want to overwrite it.
        - Wrote {path}. Compilation triggered (token=...) — the write succeeded and Unity has started recompiling. Compilation is asynchronous: call check_compilation_status afterward (polling with a short delay if it reports PENDING) before assuming the script is error-free.
        - Failed to write script: ... — the write itself failed (bad path, IO error, etc).
        """
        return await call_unity("write_unity_script", {
            "path": path,
            "content": content,
            "confirm": confirm
        })

    @mcp.tool()
    async def get_compilation_status() -> str:
        """
        Returns a single non-blocking snapshot of Unity's current compilation state. Does not wait. Call this after write_unity_script and poll again if the result is PENDING.

        Returns one of:
        - PENDING: still compiling, poll again shortly.
        - SUCCESS: compiled cleanly.
        - FAILED:\\n<file>:<line> <message> (one or more lines) — compilation errors from the most recent write. The script was written to disk even though it failed to compile.
        """
        return await call_unity("get_compilation_status")
    
    @mcp.tool()
    async def get_console_logs() -> str:
        """Returns the most recent errors and warnings from the Unity Console."""
        return await call_unity("get_console_logs")
    
    @mcp.tool()
    async def set_play_mode(enabled: bool) -> str:
        """Enters or exits Play Mode in the Unity Editor."""
        return await call_unity("set_play_mode", {"enabled": enabled})
    
    @mcp.tool()
    async def clear_console_logs() -> str:
        """Clears old Unity Editor console logs."""
        return await call_unity("clear_console_logs")
    
    @mcp.tool()
    async def inspect_gameobject(
        instance_id: Annotated[int, "Get the instance_id from the 'get_scene_hierarchy' tool output."]
    ) -> str:
        """
        Detailed inspection of a GameObject including components and public fields.
        """
        return await call_unity("inspect_gameobject", {
            "instanceId": instance_id
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
        return await call_unity("get_component_inspector_values", {
            "instanceId": instance_id,
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
        return await call_unity("get_component_code", {
            "componentName": component_name
        })

    @mcp.tool()
    async def get_physics_layers() -> str:
        """
        Returns the Unity Physics Collision Matrix.
        Shows which layers are configured to collide with each other or ignore each other.
        Essential for diagnosing 'friend or foe' collision or trigger issues.
        """
        return await call_unity("get_physics_layers")
    
    @mcp.tool()
    async def add_component(
        instance_id: Annotated[int, "The unique instance ID of the GameObject to add the component to. Get this from 'get_scene_hierarchy' or 'inspect_gameobject'."],
        component_type: Annotated[str, "Fully-qualified C# type name of the component to add. E.g. 'UnityEngine.Rigidbody', 'UnityEngine.CapsuleCollider', or a custom type like 'MyNamespace.UnitLimb'."],
        allow_duplicate: Annotated[bool, "If False (default), skips adding if a component of this type already exists and returns the existing component's instance_id. If True, adds a second instance regardless."] = False
    ) -> str:
        """
        Adds a component to a GameObject by instance_id.
        If the component already exists, behaviour is controlled by allow_duplicate.
        Returns the new (or existing) component's instance_id and the added type name.
        """
        return await call_unity("add_component", {
            "instanceId": instance_id,
            "componentType": component_type,
            "allowDuplicate": allow_duplicate
        })

    @mcp.tool()
    async def remove_component(
        instance_id: Annotated[int, "The unique instance ID of the GameObject to remove the component from. Get this from 'get_scene_hierarchy' or 'inspect_gameobject'."],
        component_type: Annotated[str, "Fully-qualified C# type name of the component to remove from the GameObject. E.g. 'UnityEngine.Rigidbody', 'UnityEngine.CapsuleCollider'."],
    ) -> str:
        """Remove a component by instance_id"""
        return await call_unity("remove_component", {
            "instanceId": instance_id,
            "componentType": component_type,
        })
    
    @mcp.tool()
    async def set_field_values(
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
        return await call_unity("set_field_values", {
            "instanceId": instance_id,
            "componentName": component_name,
            "fields": fields,
            "componentIndex": component_index
        })

    @mcp.tool()
    async def create_gameobject(
        name: Annotated[str, "Name for the new GameObject."],
        parent_instance_id: Annotated[int | None, "Optional parent GameObject instanceId. If omitted, creates at scene root."] = None,
        local_position: Annotated[dict | None, "Optional local position {x,y,z}. Applied after parenting."] = None,
        local_rotation: Annotated[dict | None, "Optional local rotation as quaternion {x,y,z,w} or euler {x,y,z} / {euler:{x,y,z}}."] = None,
        local_scale: Annotated[dict | None, "Optional local scale {x,y,z}."] = None,
    ) -> str:
        """
        Creates a new GameObject, optionally parented, with Undo support.
        Uses new GameObject(name) + Undo.RegisterCreatedObjectUndo + SetParent(parent, false).
        Returns {instanceId, path} as JSON.
        """
        params: dict = {"name": name}
        if parent_instance_id is not None:
            params["parentInstanceId"] = parent_instance_id
        if local_position is not None:
            params["localPosition"] = local_position
        if local_rotation is not None:
            params["localRotation"] = local_rotation
        if local_scale is not None:
            params["localScale"] = local_scale
        return await call_unity("create_gameobject", params)

    @mcp.tool()
    async def duplicate_gameobject(
        instance_id: Annotated[int, "instanceId of the GameObject to clone (whole hierarchy)."],
        new_name: Annotated[str | None, "Optional new name for the clone. If omitted, keeps source name."] = None,
    ) -> str:
        """
        Duplicates a GameObject hierarchy via Instantiate + Undo.RegisterCreatedObjectUndo.
        Use for cloning whole rig layers (e.g. Rig Layer (Rifle Idle)).
        Returns {instanceId, path} as JSON.
        """
        params: dict = {"instanceId": instance_id}
        if new_name is not None:
            params["newName"] = new_name
        return await call_unity("duplicate_gameobject", params)

    @mcp.tool()
    async def set_parent(
        instance_id: Annotated[int, "instanceId of the GameObject to reparent."],
        parent_instance_id: Annotated[int | None, "New parent instanceId, or null to move to scene root."] = None,
        keep_world_position: Annotated[bool, "If true, keep world position (worldPositionStays). Default false = keep local."] = False,
    ) -> str:
        """
        Reparents a GameObject via Undo.SetTransformParent.
        Pass parent_instance_id=null to unparent to root.
        """
        params: dict = {"instanceId": instance_id, "keepWorldPosition": keep_world_position}
        # Need to explicitly pass null vs omit — use dict with None handling via call_unity serialization
        params["parentInstanceId"] = parent_instance_id
        return await call_unity("set_parent", params)

    @mcp.tool()
    async def delete_gameobject(
        instance_id: Annotated[int, "instanceId of the GameObject to delete."],
    ) -> str:
        """
        Deletes a GameObject via Undo.DestroyObjectImmediate (supports Undo).
        """
        return await call_unity("delete_gameobject", {"instanceId": instance_id})

    @mcp.tool()
    async def copy_component(
        source_instance_id: Annotated[int, "instanceId of the GameObject that owns the source component."],
        source_component: Annotated[str, "Component type name to copy (e.g. 'Rig', 'MultiPositionConstraint'). Uses EditorUtility.CopySerialized for deep copy of Constraint.data structs."],
        target_instance_id: Annotated[int, "instanceId of the destination GameObject to add the copied component to."],
        source_component_index: Annotated[int, "Index if multiple components of same type exist on source (default 0)."] = 0,
    ) -> str:
        """
        Deep-copies a component from one GameObject to another via Undo.AddComponent + EditorUtility.CopySerialized.
        Required for Animation Rigging constraints because their m_Data is [Generic] and cannot be rebuilt via set_field_values.
        Returns {targetComponentIndex, targetInstanceId} as JSON.
        """
        return await call_unity("copy_component", {
            "sourceInstanceId": source_instance_id,
            "sourceComponent": source_component,
            "targetInstanceId": target_instance_id,
            "sourceComponentIndex": source_component_index
        })

    @mcp.tool()
    async def create_scriptable_object(
        type: Annotated[str, "e.g. \"Data.AssetData.UnitVisualInfoSo\", \"Data.AssetData.WeaponAssetInfoSo\", \"GridSystem.Units.GridUnitData\""],
        path: Annotated[str, "project-relative, must start \"Assets/\" and end \".asset\", e.g. \"Assets/DataScriptableObjects/Unit Visual - Rook.asset\""],
        fields: Annotated[dict | None, "optional initial field values, enum strings or instanceIds for Object refs"] = None,
        confirm: Annotated[bool, "set true to overwrite if path exists (same pattern as write_unity_script)"] = False,
    ) -> str:
        """
        Create a ScriptableObject asset via ScriptableObject.CreateInstance + AssetDatabase.CreateAsset.
        Resolves type via Type.GetType + Assembly fallback, handles Data.AssetData vs GridSystem assemblies.
        Sets fields via SerializedObject/FindProperty (primitives, enums as string \"cross\" → UnitId.cross, Vector3 as {x,y,z}, Object refs as instanceId or guid, Sprite/Mesh/Material as fileID+guid).
        Registers Undo, calls CreateAsset/SaveAssets/Refresh. Returns {instanceId, guid, path} as JSON.
        """
        params: dict = {"type": type, "path": path, "confirm": confirm}
        if fields is not None:
            params["fields"] = fields
        return await call_unity("create_scriptable_object", params)

    @mcp.tool()
    async def update_scriptable_object(
        path: Annotated[str, "Assets/DataScriptableObjects/Unit Visual - Cross.asset"],
        fields: Annotated[dict, "partial — only keys to change, e.g. {baseOutfitMaterials:[{fileID:2100000,guid:\"4cee...\"}]}"],
        add_to_array: Annotated[str | None, "optional — e.g. \"unitVisualInfoList\" to push guid into AssetLibrarySO"] = None,
    ) -> str:
        """
        Patch an existing ScriptableObject .asset without wiping other fields.
        Load via AssetDatabase.LoadAssetAtPath<Object>(path), SerializedObject → FindProperty per key (supports Material[] as fileID+guid array, enums as string, Vector3 as {x,y,z}). Only touched keys are modified; other m_Script/unitId/baseOutfitMesh preserved. Undo.RecordObject, ApplyModifiedProperties, SaveAssets, Refresh.
        If add_to_array is set, appends the provided array elements to that array field instead of replacing it.
        Returns {guid, path} as JSON.
        """
        params: dict = {"path": path, "fields": fields}
        if add_to_array is not None:
            params["addToArray"] = add_to_array
        return await call_unity("update_scriptable_object", params)

    @mcp.tool()
    async def get_recent_unity_events(
        since_seconds_ago: float = 60,
        limit: int = 50,
    ) -> str:
        """Returns Unity Editor events (hierarchy/selection/play-mode/console/objectChanged changes)
        pushed since the given time window — use this to check what changed in the
        Editor (including manual edits by the user) since your last check. objectChanged
        covers fine-grained component/property edits (add_component, remove_component,
        set_field_values and human Inspector edits) via ObjectChangeEvents.changesPublished."""
        events = events_buffer.get_recent_events(since=time.time() - since_seconds_ago, limit=limit)
        if not events:
            return "No Unity events recorded in that window."
        return json.dumps(events, indent=2)
    
    @mcp.tool()
    async def get_lights_affecting_object(
        instance_id: Annotated[int, "The instance_id of the GameObject. Obtain this from get_scene_hierarchy."],
    ) -> str:
        """
        Finds all scene lights and determines if they overlap or illuminate a specific GameObject.
        
        Returns:
        - All Light components in the scene
        - For each light: name, type, intensity, range, position, lightmapping mode, culling mask, rendering layer mask
        - Distance from each light to the target object
        - Whether the target is within range of each light
        - Total count of lights in range (to spot per-object limit issues)
        """
        return await call_unity("get_lights_affecting_object", {
            "instanceId": instance_id
        })
    
    @mcp.tool()
    async def get_urp_pipeline_settings() -> str:
        """Returns the URP render pipeline asset's current render path setting"""
        return await call_unity("get_urp_pipeline_settings")

    @mcp.tool()
    async def diagnose_lighting_issue(
        instance_id: Annotated[int, "The instance_id of the GameObject with lighting issues. Get this from 'get_scene_hierarchy'."],
        issue_description: Annotated[str, "Description of the lighting problem (e.g., 'object appears too dark', 'shadows not rendering', 'lights not affecting object')"],
        max_iterations: Annotated[int, "Maximum diagnostic tool rounds before stopping (default 10)"] = 10,
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

        Uses a server-owned LLM via core.llm_provider.get_diagnostic_llm() — no MCP
        sampling required. Configure the provider/model via LIGHTING_LLM_PROVIDER
        and LIGHTING_LLM_MODEL env vars and set the provider's API key
        (e.g. ANTHROPIC_API_KEY, OPENAI_API_KEY); the tool will return
        "Error: ..." if credentials are missing or invalid.
        """
        global _lighting_agent

        if _lighting_agent is None:
            unity_tools = {
                "get_lights_affecting_object": get_lights_affecting_object,
                "get_urp_pipeline_settings": get_urp_pipeline_settings,
                "get_component_inspector_values": get_component_inspector_values,
                "inspect_gameobject": inspect_gameobject,
                "get_screenshot": get_screenshot,
            }
            _lighting_agent = LightingDiagnosticAgent(unity_tools, max_iterations=max_iterations)
        else:
            _lighting_agent.max_iterations = max_iterations

        return await _lighting_agent.diagnose_lighting_issue(
            instance_id,
            issue_description,
        )
