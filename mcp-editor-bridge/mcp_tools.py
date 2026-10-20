from unity_bridge import call_unity
from batch_requests import BatchLimits, BatchOperation, PropertyPaths
from typing import Annotated, Literal
from pydantic import Field
from fastmcp.utilities.types import Image
import asyncio
import base64
import json
import time
from events import events_buffer, registration

def register_unity_tools(mcp):
    """Registers all Unity-specific tools to the provided MCP instance."""

    @mcp.tool()
    async def acquire_editor_references(
        instance_ids: Annotated[list[int], Field(min_length=1, max_length=64), "1–64 live GameObject/component IDs from existing hierarchy tools."],
        limits: BatchLimits | None = None,
    ) -> str:
        """Acquire opaque references for bounded semantic batches. Reacquire after reload,
        scene/stage changes, deletion or cache eviction. Asset IDs are rejected.
        Acquisition uses current IDs: verify returned identities before mutation.
        """
        return await call_unity("acquire_editor_references", {
            "instanceIds": instance_ids,
            **({"limits": limits.model_dump()} if limits is not None else {}),
        })

    @mcp.tool()
    async def inspect_prefab_assets(
        paths: Annotated[list[str], Field(min_length=1, max_length=32), "1–32 imported Assets/ or Packages/ .prefab paths."],
        component: Annotated[str, "Component type filter, preferably fully qualified, e.g. UnityEngine.CharacterJoint."],
        properties: PropertyPaths,
        limits: BatchLimits | None = None,
    ) -> str:
        """Read selected serialized properties throughout resolved imported prefab hierarchies,
        including variants, nested prefabs and inactive objects. Returns structured numeric
        values, sibling-indexed paths and component identities, with explicit truncation.
        Does not instantiate, select, save, reimport or modify scenes/assets. Asset identities
        are descriptive, not batch mutation references. Does not include unsaved stage edits.
        """
        return await call_unity("inspect_prefab_assets", {
            "paths": paths, "component": component, "properties": properties,
            **({"limits": limits.model_dump()} if limits is not None else {}),
        })

    @mcp.tool()
    async def semantic_batch(
        operations: Annotated[list[BatchOperation], Field(min_length=1, max_length=64), "1–64 ordered operations with unique IDs. Use either all inspect or all set_fields."],
        dry_run: bool = False,
        name: Annotated[str, "Undo group name for a compatible mutation batch."] = "USB semantic batch",
        limits: BatchLimits | None = None,
    ) -> str:
        """Bounded inspection or serialized property edits using opaque Editor references.
        Reads continue after errors. Mutations preflight the entire request, then revalidate
        each operation and stop on first failure; partial application is possible. One named
        Undo group is not atomic. dry_run stages and discards values without applying them.
        Optional expected maps use exact typed value comparisons. No saves or mutation retries.
        Object values use null, {reference: token}, or explicit asset path/GUID references.
        Targets must stay in one editable scene/Prefab Stage (Auto Save off). Reports submitted
        property writes; project validation callbacks may cause additional effects.
        """
        return await call_unity("semantic_batch", {
            "operations": [op.model_dump(exclude_none=True) for op in operations],
            "dryRun": dry_run, "name": name,
            **({"limits": limits.model_dump()} if limits is not None else {}),
        }, retry_connect=not any(op.op == "set_fields" for op in operations))

    @mcp.tool()
    async def get_project_settings(
        sections: Annotated[list[str] | None, "Which sections to return. Valid: 'core', 'rendering', 'input', 'ui', 'scripting', 'tags_layers', 'physics', 'editor_prefs'. Omit or pass empty for all."] = None,
    ) -> str:
        """
        Returns Unity Project Settings as JSON. Selectively fetch sections to keep output small.

        Sections:
        - core: Unity version, editor platform, active build target/group, company/product/bundleVersion
        - rendering: active pipeline (Built-in/URP/HDRP), URP asset path, color space, MSAA/quality, plus URP details (rendering path, per-object light limit)
        - input: Active Input Handling (Old/New/Both) and default InputActionAsset assets if any
        - ui: uGUI vs UI Toolkit signals — EventSystem/Canvas in open scenes, UIDocument/VisualTreeAsset/StyleSheet counts
        - scripting: API compatibility level, #define symbols (group), allowUnsafeCode, scripting backend
        - editor_prefs: current Editor Interaction Mode, idle time, setter support and restore availability (user preferences, not project assets)
        - tags_layers: Tag list and layer names/index map
        - physics: 3D layer collision matrix as {layerCollisions: {layer: [layers it collides with]}}.
          Use it to diagnose collisions or triggers that never fire, or that hit the wrong side
          ('friend or foe'): check both objects' layers (get_scene_hierarchy include_layers=True).

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
    async def set_editor_throttling(
        mode: Annotated[Literal["no_throttling", "default", "restore"], "Interaction Mode override, or restore the setting saved before the first override in this Editor session."],
    ) -> str:
        """Explicitly change Unity's user-level Interaction Mode on the main thread.

        Recommended for long-running tasks that interact with Unity: set mode="no_throttling" before starting, then set mode="default" when the task finishes, including on failure or cancellation. Use mode="restore" instead if you need to preserve a prior custom setting.

        no_throttling disables frame idling; default restores Unity's default preference. restore restores the original mode and idle time (including Custom/Monitor modes).
        Repeated overrides retain the first restore point across domain reloads, not Editor restarts. Changes persist until explicitly changed; restore before quitting if temporary.
        Returns JSON with requestedMode, changed and editor_prefs, or Error: ... .
        No Throttling can increase CPU/power use. Does not guarantee unfocused Editor updates.
        Does not change Play Mode, refresh assets, or save scenes.
        """
        return await call_unity("set_editor_throttling", {"mode": mode})

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
        include_components: Annotated[bool, "If true, includes component names on each GameObject (e.g. 'Rigidbody', 'UnitHealth'). Required if you need to know what components exist before calling inspect_gameobject with a component."] = True,
        include_positions: Annotated[bool, "If true, includes world-space position for each object. Omit if not needed to reduce output size."] = True,
        only_main_cam_visible: Annotated[bool, "If true, objects out of the main camera view will be culled."] = False,
        root_instance_id: Annotated[int | None, "Optional instance_id to start traversal from instead of the scene root — e.g. a UI Canvas root, a character, or any subtree found via a previous call. depth/max_nodes apply relative to this root and paths are relative to it. Get the id from a prior get_scene_hierarchy call."] = None,
        prune_skinned: Annotated[bool, "If true (default), skips objects with a SkinnedMeshRenderer and their children so character meshes don't flood the result. Set false to walk a character's rig."] = True,
    ) -> str:
        """
        Returns the current Unity scene hierarchy as a list of GameObjects with their paths and instance_ids.

        This is usually the FIRST tool to call — use it to discover GameObjects and their instance_ids, which are required by inspect_gameobject.

        Inspecting one object's subtree, including a character's skeleton/rig:
        1. Find the object's instance_id with a shallow call (default depth=2).
        2. Call again with root_instance_id=<that id>, prune_skinned=False and a deep depth
           (e.g. depth=15; rigs nest Hips > Spine > ... > fingers), include_positions=False
           unless you need bone positions.
        3. Pass a bone's instance_id to inspect_gameobject for its Transform/constraint values.

        Tip: set include_components=True to confirm a component exists on a GameObject before inspecting it.
        Tip: for a specific known subtree (e.g. a UI Canvas), pass its instance_id as root_instance_id instead of raising max_nodes to reach it from the scene root.
        """
        return await call_unity("get_scene_hierarchy", {
            "depth": depth,
            "maxNodes": max_nodes,
            "includeLayers": include_layers,
            "includeComponents": include_components,
            "includePositions": include_positions,
            "onlyMainCamVisible": only_main_cam_visible,
            "rootInstanceId": root_instance_id,
            "pruneSkinned": prune_skinned,
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
    async def author_ui(
        operations: Annotated[list[dict], "Ordered uGUI operations: create, add_component, configure, set_rect, set_fields, button_on_click. References: {instanceId}, {guid,fileID}, {path}, or {ref: earlier_create_id}; add component/componentIndex to select a component. See docs/ugui-authoring.md."],
        name: Annotated[str, "Name for the single Unity Undo group."] = "Author uGUI",
    ) -> str:
        """Create/revise ordinary uGUI in one Edit Mode transaction on Unity's main thread.

        create: {op,id,kind,name,parent?,properties?,rect?}; kind is Canvas, Panel,
        Image, Button, TextMeshProUGUI or RectTransform. Canvas requires renderMode;
        camera/world-space also requires worldCamera. TMP requires explicit font.
        add_component: {op,target,component,properties?}; layout components/CanvasGroup.
        configure: {op,target: component_reference,properties}; allowlisted public UI properties.
        set_rect: {op,target: gameobject_reference,rect,allowDriven?}; anchors/pivot first,
        then position/size OR offsets, then local Euler rotation/scale. Driven edits rejected by default.
        set_fields: {op,target: component_reference,fields}; typed references/arrays only.
        button_on_click: {op,target: Button_reference,mode: append|replace,listeners};
        listeners use {target,method,mode: void|int|float|bool|string|object,argument?,argumentType?,state?}.
        Validates before mutation where possible; execution failures revert the Undo group.
        Returns local-id -> object/component instance IDs, global IDs and undoToken. No save.
        Existing instance-ID tools remain usable; global IDs persist only after saving.
        """
        return await call_unity("author_ui", {"operations": operations, "name": name})

    @mcp.tool()
    async def inspect_ui(
        instance_id: Annotated[int, "Root GameObject instance ID with RectTransform."],
        camera_instance_id: Annotated[int | None, "Optional viewing Camera/component or GameObject ID for world-space visibility."] = None,
        max_nodes: Annotated[int, "Maximum hierarchy nodes, 1–1000."] = 100,
        include_references: Annotated[bool, "Include up to 100 Inspector-visible serialized object references per node."] = False,
    ) -> str:
        """Inspect calculated uGUI layout, screen bounds, visibility estimates, interaction,
        CanvasGroup restrictions, persistent callbacks and layout drivers. Rectangle overlap
        does not prove input blocking: use raycast_ui. Does not save or author properties."""
        params = {"instanceId": instance_id, "maxNodes": max_nodes, "includeReferences": include_references}
        if camera_instance_id is not None:
            params["cameraInstanceId"] = camera_instance_id
        return await call_unity("inspect_ui", params)

    @mcp.tool()
    async def raycast_ui(
        x: Annotated[float, "Primary-display screen pixel X, origin bottom-left."],
        y: Annotated[float, "Primary-display screen pixel Y, origin bottom-left."],
        camera_instance_id: Annotated[int | None, "Optional viewing camera to compare rendering visibility with actual raycast hits."] = None,
    ) -> str:
        """Query actual raycasters, returning each hit's object, camera and raycaster.
        Diagnoses camera-mask-hidden UI hits. Uses EventSystem ordering when available;
        without one, reports per-raycaster order. Does not synthesize clicks or consume input."""
        params = {"x": x, "y": y}
        if camera_instance_id is not None:
            params["cameraInstanceId"] = camera_instance_id
        return await call_unity("raycast_ui", params)

    @mcp.tool()
    async def undo_ui_batch(undo_token: Annotated[str, "undoToken from the untouched most recent author_ui result."]) -> str:
        """Undo the latest UI batch only if no intervening Undo activity occurred.
        Otherwise use Unity's Undo history. Tokens expire on reload or explicit save."""
        return await call_unity("undo_ui_batch", {"undoToken": undo_token})

    @mcp.tool()
    async def save_ui_context(
        instance_id: Annotated[int, "GameObject ID in the scene or current Prefab Stage to save."],
        path: Annotated[str | None, "New Assets/.../*.unity path for an unsaved scene only. Existing contexts save at their existing path."] = None,
    ) -> str:
        """Explicitly save the entire containing scene or current prefab contents, including
        other pending edits. Rejects package/vendor paths and Prefab Mode Auto Save.
        Does not apply scene prefab-instance overrides back to prefab assets."""
        params = {"instanceId": instance_id}
        if path is not None:
            params["path"] = path
        return await call_unity("save_ui_context", params)

    @mcp.tool()
    async def inspect_model_asset(
        path: Annotated[str, "Imported model path in the connected Unity project, e.g. Assets/Models/Ship.fbx (Assets/ or Packages/)."],
        include_details: Annotated[bool, "Include per-submesh topology/index counts, extra importer settings, other embedded subassets and recursive rather than direct dependencies."] = False,
    ) -> str:
        """Read-only inspection of an imported FBX/OBJ or other ModelImporter asset.

        Returns mesh GUID/local file IDs, vertex/submesh counts, mesh-local bounds,
        imported hierarchy/local transforms, hierarchy-transformed placement bounds,
        scale/units/axis settings, material slots and embedded/external dependencies.
        Bounds use imported Unity units; placement bounds include the root transform
        in an identity parent frame, not a scene instance or animated/skinned pose.
        Does not instantiate, reimport, modify assets/scenes, or change selection.
        """
        return await call_unity("inspect_model_asset", {"path": path, "include_details": include_details})

    @mcp.tool()
    async def bake_layout(
        path: Annotated[str | None, "Optional project-relative .json path under Assets/ to also write the layout to, e.g. Assets/Settings/Levels/CatalogTest.layout.json."] = None,
        scene: Annotated[str | None, "Project path of an open scene to bake, e.g. Assets/Scenes/CatalogTest.unity. Omit for the active scene."] = None,
    ) -> str:
        """Read a scene (hand-made or applied) back into compact layout JSON.

        Needs the game's IGridRulesAdapter. Blocking prefab instances become pieces
        [localId, prefabGuid, x, y, rot]; other on-board prefab instances become decor;
        deploy cells and enemy spawns come from the level. Off-grid rotation/scale/position, off-board and
        non-prefab blockers are reported as cell-addressed lines, never snapped. Also lists
        cells where the layout's footprints disagree with the game's own occupancy bake.
        Edit Mode only. Without path it changes nothing; with path it writes only that file.
        """
        params: dict = {}
        if path is not None:
            params["path"] = path
        if scene is not None:
            params["scene"] = scene
        return await call_unity("bake_layout", params)

    @mcp.tool()
    async def get_layout(
        path: Annotated[str, "Project-relative layout file under Assets/, e.g. Assets/Settings/Levels/CatalogTest.layout.json."],
    ) -> str:
        """Read a layout JSON file: a one-line summary (size and counts) plus the canonical JSON,
        or the schema error with where it is. Changes nothing.
        Layout format: {"v":1,"size":[w,h],"pieces":[[localId,prefabGuid,x,y,rot]],"deploy":[[x,y]],
        "enemies":[[localId,profileGuid,visualGuid,x,y,facingDegrees]],"decor":[[localId,prefabGuid,x,y,rot]]};
        pieces anchor at their min corner, rot is 0-3 quarter turns (docs/grid-layouts.md).
        """
        return await call_unity("get_layout", {"path": path})

    @mcp.tool()
    async def set_layout(
        path: Annotated[str, "Project-relative .json path under Assets/ to write (its folder must exist)."],
        layout: Annotated[str | dict, "The layout JSON (text or object). A prefab path (Assets/....prefab) may stand where a GUID goes; it is written as the GUID."],
        validate: Annotated[bool, "Run the grid tier (validate_layout tier='grid') on the written layout and append its report."] = True,
        max_lines: Annotated[int, "Report issue lines to show (0 = all)."] = 40,
        ascii: Annotated[bool, "Append the report's ASCII map of the layout."] = False,
        metrics: Annotated[bool, "With validate: also append the level metrics (see validate_layout)."] = False,
    ) -> str:
        """Schema-check a layout and write it to a file in canonical form (one piece per line), then
        import it. A schema error writes nothing. Replaces an existing file. Touches no scene;
        use apply_layout to build it into a scene.
        """
        return await call_unity("set_layout", {
            "path": path, "layout": layout, "validate": validate, "max_lines": max_lines, "ascii": ascii,
            "metrics": metrics,
        }, timeout=60.0, retry_connect=False)

    @mcp.tool()
    async def validate_layout(
        path: Annotated[str | None, "Layout file under Assets/ to check (tier 'grid'). Give this or layout."] = None,
        layout: Annotated[str | dict | None, "Inline layout JSON to check (tier 'grid'); prefab paths may stand for GUIDs."] = None,
        tier: Annotated[Literal["grid", "scene"], "'grid': the draft alone, without touching any open scene (pieces without a catalog card are measured on a hidden preview instance). 'scene': an open scene, hand-made or applied, against the game's own occupancy bake (Edit Mode)."] = "grid",
        scene: Annotated[str | None, "tier 'scene' only: project path of an open scene. Omit for the active scene."] = None,
        ascii: Annotated[bool, "Append an ASCII map (# full, h half, o blocked, . open, A deploy, E enemy, - unreachable)."] = False,
        max_lines: Annotated[int, "Issue lines to show (0 = all); also caps the metrics lines."] = 40,
        metrics: Annotated[bool, "Append level metrics (LAYOUT_METRICS): per enemy DISTANCE (turns), COVER_NEAR, ROUTES, OPEN_GROUND; COVER_NEAR_DEPLOY; SIGHTLINES over the engagement region. Judgement, not errors."] = False,
        metrics_only: Annotated[bool, "Metrics plus only the report's header line (implies metrics)."] = False,
        region: Annotated[list[int] | None, Field(min_length=4, max_length=4), "Metrics region [x0, y0, x1, y1] in cells, inclusive. Omit for the deploy cells and spawns plus a margin."] = None,
    ) -> str:
        """Validate a level layout. Report: LAYOUT_REPORT header, then 'CODE x,y message' lines,
        errors first, then warnings and notes. The grid tier covers structure (bounds, overlap,
        walls), encounter (deploy/enemy cells, facing), reachability, scale against the game's
        cover heights and plausibility (ground runs, building fronts and doors). Collider fit and
        the game's occupancy need apply_layout or tier='scene'. With metrics, a LAYOUT_METRICS
        section follows on the same grid (values outside the game's targets listed first).
        """
        params: dict = {"tier": tier, "ascii": ascii, "max_lines": max_lines, "metrics": metrics, "metrics_only": metrics_only}
        if region is not None:
            params["region"] = region
        if path is not None:
            params["path"] = path
        if layout is not None:
            params["layout"] = layout
        if scene is not None:
            params["scene"] = scene
        return await call_unity("validate_layout", params, timeout=60.0)

    @mcp.tool()
    async def apply_layout(
        path: Annotated[str | None, "Layout file under Assets/ to apply. Give this or layout."] = None,
        layout: Annotated[str | dict | None, "Inline layout JSON to apply; prefab paths may stand for GUIDs."] = None,
        scene: Annotated[str | None, "Project path of the open scene to build into. Omit for the active scene."] = None,
        validate: Annotated[bool, "Append the full report after apply: grid tier plus the physics tier (the game's occupancy bake of the result)."] = True,
        ascii: Annotated[bool, "Append the report's ASCII map."] = False,
        max_lines: Annotated[int, "Issue and note lines to show (0 = all)."] = 40,
    ) -> str:
        """Build a layout into an open scene: prefab instances by GUID under one [Generated Layout]
        root (replacing only that root), at their catalog cards' scale and footprint, plus the
        level's deploy cells and enemy spawns. One Undo group. Edit Mode only. The layout size must
        equal the level's board. Never saves: the scene (and level asset) are left dirty for the owner.
        """
        params: dict = {"validate": validate, "ascii": ascii, "max_lines": max_lines}
        if path is not None:
            params["path"] = path
        if layout is not None:
            params["layout"] = layout
        if scene is not None:
            params["scene"] = scene
        return await call_unity("apply_layout", params, timeout=60.0, retry_connect=False)

    @mcp.tool()
    async def create_layout_scene(
        template: Annotated[str, "Project path of an existing level scene to copy, e.g. Assets/Scenes/CatalogTest.unity (NK)."],
        path: Annotated[str, "Project path for the new scene (under Assets/, ending .unity; must not exist; its folder must)."],
        level: Annotated[str | None, "Where the copy of the template's level asset goes. Omit for beside the template's level asset, named after the new scene."] = None,
        size: Annotated[list[int] | None, Field(min_length=2, max_length=2), "New board size [width, height] in cells. Omit for the template's."] = None,
    ) -> str:
        """Start a new level from a template level scene: copies the scene (managers, cameras, UI,
        lighting) and its level asset, repoints the copy to the new level so applying to it never
        rewrites the template's, removes the template's [Generated Layout], and sets the board size.
        Saves the new scene and level; does not open the scene or touch Build Settings (open it with
        open_scene, then apply_layout). On failure nothing is left behind. Edit Mode only.
        """
        params: dict = {"template": template, "path": path}
        if level is not None:
            params["level"] = level
        if size is not None:
            params["size"] = size
        return await call_unity("create_layout_scene", params, timeout=60.0, retry_connect=False)

    # output_schema=None: a list of text plus image is content, not structured output (a `-> list` annotation
    # would make FastMCP declare a schema and reject the image).
    @mcp.tool(output_schema=None)
    async def render_layout_topdown(
        scene: Annotated[str | None, "Project path of an open scene. Omit for the active scene."] = None,
        cells: Annotated[list[int] | None, Field(min_length=4, max_length=4), "Region [x0, y0, x1, y1] in cells, inclusive. Omit for the whole board."] = None,
        margin_cells: Annotated[int, "Cells of margin around the region."] = 1,
        grid: Annotated[bool, "Draw cell lines (every 5th yellow, board edge red, cell 0,0 marked with a cyan L)."] = True,
        markers: Annotated[bool, "Outline the level's deploy cells (blue) and enemy spawns (red)."] = True,
        cut_height: Annotated[float | None, "Meters above the floor: clip everything higher (roofs, upper floors) to see inside buildings, e.g. 2.4."] = None,
        max_width: Annotated[int, "Image width in pixels (64-4096)."] = 1280,
        jpg_quality: Annotated[int, "JPEG quality, 1-100."] = 70,
    ) -> list:
        """Orthographic capture straight down onto the board (or a cell region) of a scene with a
        grid-rules adapter. Image right is grid +x and up is grid +y, like the validators' ASCII maps.
        Uses a temporary hidden camera; the scene is not changed. Returns a caption and the image.
        """
        params: dict = {"margin_cells": margin_cells, "grid": grid, "markers": markers,
                        "max_width": max_width, "jpg_quality": jpg_quality}
        if scene is not None:
            params["scene"] = scene
        if cells is not None:
            params["cells"] = cells
        if cut_height is not None:
            params["cut_height"] = cut_height
        result = await call_unity("render_layout_topdown", params, timeout=60.0)
        try:
            data = json.loads(result)
        except json.JSONDecodeError:
            raise RuntimeError(result)
        return [data["caption"], Image(data=base64.b64decode(data["image"]), format="jpeg")]

    @mcp.tool()
    async def query_level_catalog(
        role: Annotated[list[Literal["blocker", "cover_half", "cover_full", "ground", "decor", "building", "unknown"]] | None, "Roles to keep. Omit for all."] = None,
        cover: Annotated[Literal["None", "Half", "Full"] | None, "Cover the piece gives."] = None,
        max_cells: Annotated[list[int] | None, Field(min_length=2, max_length=2), "Largest footprint [w, h] in cells; matches in either orientation."] = None,
        min_cells: Annotated[list[int] | None, Field(min_length=2, max_length=2), "Smallest footprint [w, h] in cells; matches in either orientation."] = None,
        name: Annotated[str | None, "Case-insensitive substring of the card id (pack/folder/prefab path), e.g. 'Sandbag'."] = None,
        source: Annotated[Literal["auto", "catalog", "reviewed"], "'catalog': <Project>/AssetCatalog/level-catalog.json (Asset Preview). 'reviewed': the per-piece entries of AssetCatalog/LevelCatalogOverrides.json, carded from their prefabs (a game project such as NK: the pieces usable in a layout). 'auto': the catalog file when present, else reviewed."] = "auto",
        cell_size: Annotated[float | None, "Cell size in meters for footprints. Omit for the game's (adapter), else 2."] = None,
        limit: Annotated[int, "Rows per page (1-500)."] = 50,
        offset: Annotated[int, "Rows to skip (paging)."] = 0,
    ) -> str:
        """Find level pieces for a layout. Returns which file was read, how many cards match, and one
        TSV row per card sorted by id: guid, role (+walls for enterable buildings), cover, cells at
        rot 0 (WxH), height in meters at placementScale, scale, id (and the prefab path for reviewed
        pieces). Use the guid (or path) in layout pieces; describe_level_cards has full card details.
        Read-only.
        """
        params: dict = {"source": source, "limit": limit, "offset": offset}
        if role is not None:
            params["role"] = role
        if cover is not None:
            params["cover"] = cover
        if max_cells is not None:
            params["max_cells"] = max_cells
        if min_cells is not None:
            params["min_cells"] = min_cells
        if name is not None:
            params["name"] = name
        if cell_size is not None:
            params["cell_size"] = cell_size
        return await call_unity("query_level_catalog", params, timeout=60.0)

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
        - Wrote {path}. Compilation triggered (token=...) — the write succeeded and Unity has started recompiling. Compilation is asynchronous: call get_compilation_status afterward (polling with a short delay if it reports PENDING) before assuming the script is error-free.
        - Failed to write script: ... — the write itself failed (bad path, IO error, etc).
        """
        return await call_unity("write_unity_script", {
            "path": path,
            "content": content,
            "confirm": confirm
        })

    @mcp.tool()
    async def refresh_assets() -> str:
        """Make Unity import files you edited outside it, then compile and reload if scripts changed.

        Call after editing project files with your own tools: Unity does not notice external
        edits until its window is focused, and this works while the Editor is in the background.
        Not needed after write_unity_script, which imports its own file.
        No arguments. Returns a token and RELOAD_IMMINENT hint (reload is conditional),
        or BUSY without starting work. Poll get_compilation_status until terminal.
        NO_COMPILATION means no new compiler result, not proof that scripts compile.
        Does not rewrite files, force reload, change Play Mode, or save scenes.
        """
        return await call_unity("refresh_assets")

    @mcp.tool()
    async def get_compilation_status() -> str:
        """
        Returns a single non-blocking snapshot of Unity's current compilation state. Does not wait. Call this after write_unity_script or refresh_assets and poll again if the result is PENDING.

        Returns one of:
        - PENDING: refresh/import/compilation in progress, poll again shortly.
        - SUCCESS: compiled cleanly.
        - NO_COMPILATION: refresh completed without a new compilation result.
        - UNKNOWN: no compilation result recorded this Editor session.
        - FAILED:\\n<file>:<line> <message> (one or more lines) — latest compilation/refresh errors, retained until a new compilation result replaces them.
        """
        return await call_unity("get_compilation_status")
    
    @mcp.tool()
    async def get_console_logs(
        clear: Annotated[bool, "If true, clears the Console after reading, so the next call returns only entries logged since."] = False,
    ) -> str:
        """Returns the first line of the last 10 Unity Console entries.

        To see only what one action logs (e.g. a Play Mode run or invoke_static), call with
        clear=True first, run the action, then call again.
        """
        return await call_unity("get_console_logs", {"clear": clear})
    
    def _play_transport_failed(result: str) -> bool:
        low = result.lower()
        return ("not connected" in low or "timed out processing" in low
                or "timed out responding" in low)

    async def _settle_via_poll(kind: str, timeout_s: float, first_result: str) -> str:
        """Fallback when the enter/exit request's own connection dropped
        across domain reload: poll poll_play_state until the pending op
        clears or the overall deadline passes. Always terminal."""
        deadline = time.time() + timeout_s
        last = first_result
        want_playing = kind == "enter"
        while time.time() < deadline:
            await asyncio.sleep(1.0)
            state = await call_unity("poll_play_state", {}, timeout=10.0)
            if _play_transport_failed(state):
                last = state
                continue
            last = state
            try:
                snapshot = json.loads(state)
            except (json.JSONDecodeError, TypeError):
                return state
            if snapshot.get("pendingOp") is not None:
                continue
            playing = bool(snapshot.get("isPlaying"))
            settled = playing == want_playing and not snapshot.get("transitioning", False)
            verb = "entered" if want_playing else "exited"
            if settled:
                return f"Play Mode {verb} (settled via poll after reload): {state}"
            return f"Play Mode op settled without reaching {'Play' if want_playing else 'Edit'} Mode: {state}"
        return (f"TIMEOUT after {timeout_s:g}s waiting for {'Play' if want_playing else 'Edit'} Mode. "
                f"If the Editor window is unfocused, set_editor_throttling no_throttling and retry. Last state: {last}")

    @mcp.tool()
    async def open_scene(
        path: Annotated[str, "Project-relative scene path, e.g. 'Assets/Scenes/GridBattle.unity'."],
        dirty_policy: Annotated[str, "'fail' (default): refuse when the active scene has unsaved changes. 'save': save open scenes first. Unsaved work is never silently discarded."] = "fail",
    ) -> str:
        """Open a scene by project-relative path. Returns scene path + dirty state."""
        return await call_unity("open_scene", {"path": path, "dirty_policy": dirty_policy})

    @mcp.tool()
    async def poll_play_state() -> str:
        """Return {isPlaying, transitioning, isCompiling, isUpdating, pendingOp} with no side effects."""
        return await call_unity("poll_play_state", {})

    @mcp.tool()
    async def enter_play(
        timeout_s: Annotated[float, "Seconds to wait for Play Mode (1–55; capped for the 60s transport)."] = 30,
    ) -> str:
        """Request Play Mode, then wait until isPlaying with no transition and no compile/update busy, or timeout. Survives domain reload via poll fallback. Always terminal.
        Unless the user prefers otherwise, always run this under set_editor_throttling("no_throttling") and restore afterwards: an unfocused Editor on default throttling can stall the transition for minutes."""
        timeout_s = min(max(float(timeout_s), 1.0), 55.0)
        result = await call_unity("enter_play", {"timeout_s": timeout_s},
                                  timeout=min(timeout_s + 15.0, 60.0), retry_connect=False)
        if _play_transport_failed(result):
            return await _settle_via_poll("enter", timeout_s, result)
        return result

    @mcp.tool()
    async def exit_play(
        timeout_s: Annotated[float, "Seconds to wait for Edit Mode (1–55; capped for the 60s transport)."] = 30,
    ) -> str:
        """Request Edit Mode, then wait until restored with no transition and no compile/update busy, or timeout. Survives domain reload via poll fallback. Always terminal.
        Unless the user prefers otherwise, always run this under set_editor_throttling("no_throttling") and restore afterwards: an unfocused Editor on default throttling can stall the transition for minutes."""
        timeout_s = min(max(float(timeout_s), 1.0), 55.0)
        result = await call_unity("exit_play", {"timeout_s": timeout_s},
                                  timeout=min(timeout_s + 15.0, 60.0), retry_connect=False)
        if _play_transport_failed(result):
            return await _settle_via_poll("exit", timeout_s, result)
        return result

    @mcp.tool()
    async def invoke_menu_item(
        menu_path: Annotated[str, "Exact menu path, e.g. 'Tools/Grid Combat/Validate Action Points'. No fuzzy matching."],
    ) -> str:
        """Execute one Editor menu item by exact path via EditorApplication.ExecuteMenuItem.
        Unknown paths return a not-found error with no partial execution.
        Refused while playing, compiling, or updating. Each invocation is logged to the Console."""
        return await call_unity("invoke_menu_item", {"menuPath": menu_path}, retry_connect=False)

    @mcp.tool()
    async def invoke_static(
        type_name: Annotated[str, "Exact full type name, e.g. 'MyNamespace.MyValidator'."],
        method_name: Annotated[str, "Zero-arg public static method name. No overload guessing, no instance construction, no generics."],
    ) -> str:
        """Invoke exactly one public static zero-arg non-generic method by exact type and method names.
        Returns the method's return value as a string ("ok" for void, "null" for null).
        Unknown types/methods return not-found errors; thrown exceptions return "Error: <type>: <message>".
        Refused while playing, compiling, or updating. Each invocation is logged to the Console."""
        return await call_unity("invoke_static", {"typeName": type_name, "methodName": method_name},
                                retry_connect=False)
    
    @mcp.tool()
    async def inspect_gameobject(
        instance_id: Annotated[int, "The instance_id of the GameObject. Get it from get_scene_hierarchy."],
        component: Annotated[str | None, "Optional exact component class name (e.g. 'UnitHealth', 'Rigidbody'). Omit to inspect every component on the GameObject."] = None,
    ) -> str:
        """
        Returns the serialized field values currently visible in the Unity Inspector for a GameObject:
        every component by default, or only `component` when given. Includes [SerializeField]
        private fields and prefab overrides — live Editor values that may differ from source
        code defaults — and expands Generic structs such as Animation Rigging constraint data.

        Reading one component's values:
        1. Call get_scene_hierarchy (include_components=True) to find the GameObject's instance_id
           and confirm the component name.
        2. Call this tool with that instance_id and component=<name>. This keeps the output small
           on objects with many components.

        To read the component's source logic instead, grep for `class <Name>` (see the server
        instructions for where package sources live).
        """
        params: dict = {"instanceId": instance_id}
        if component is not None:
            params["componentName"] = component
        return await call_unity("inspect_gameobject", params)

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
        component_name: Annotated[str, "Name of the component type whose fields will be set. E.g. 'Rigidbody', 'CapsuleCollider', 'UnitLimb'. Use inspect_gameobject with component=<name> to see available field names."],
        fields: Annotated[dict, "Key-value map of field names to new values. Values may be: primitives (float, int, bool, string), structs as dicts (e.g. {'x':0,'y':1,'z':0}), enum strings, or integer instance_ids for Object reference fields."],
        component_index: Annotated[int, "Zero-based index to disambiguate when multiple components of the same type exist on the GameObject. Defaults to 0 (first found)."] = 0
    ) -> str:
        """
        Sets one or more serialized field values on a component attached to a GameObject.
        Records an Undo operation so changes are reversible in the Editor.
        Use inspect_gameobject with component=<name> first to discover field names and current values.
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
    async def generate_asset_catalog(
        scan_roots: Annotated[list[str] | None, "Project-relative roots to scan, e.g. ['Assets/Synty']. Omit for the default (Assets/Synty, falling back to Assets)."] = None,
        thumbnails: Annotated[bool, "Capture AssetPreview PNG thumbnails into <Project>/AssetCatalog/thumbs/ (level-thumbs/ for the level catalog)."] = True,
        catalog: Annotated[str, "'assets' (default): the asset catalog. 'level': level cards (role, bounds, placementScale) for prefabs into <Project>/AssetCatalog/level-catalog.json, keyed by GUID; see docs/level-catalog.md."] = "assets",
    ) -> str:
        """
        Starts a frame-pumped Editor-side scan of content-pack assets (large
        packs take minutes — the Editor stays responsive with progress + cancel).
        Covers .fbx (with Mesh sub-asset refs + bone counts), .prefab (resolved
        skinned/static mesh composition) and .mat (pack-wide color variants with
        albedo) records in <Project>/AssetCatalog/asset-catalog.json. Re-runs
        upsert by id (no dupes) and reuse existing thumbnails. Returns {runId,
        state, total} immediately — poll get_asset_catalog_status for {jsonPath,
        itemCount, thumbnailCount} on completion. With catalog='level' it scans
        .prefab files only, measures each on isolated prefab contents and writes
        level cards (characters, weapons and FX are left out) without touching
        asset-catalog.json; the status then also has per-role counts.
        """
        params: dict = {"thumbnails": thumbnails, "catalog": catalog}
        if scan_roots is not None:
            params["scan_roots"] = scan_roots
        return await call_unity("generate_asset_catalog", params)

    @mcp.tool()
    async def describe_level_cards(
        paths: Annotated[list[str], "Project-relative .prefab paths, e.g. ['Assets/Synty/PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_01.prefab']."],
        cell_size: Annotated[float, "Grid cell size in meters for the computed footprint (NK: 2)."] = 2.0,
    ) -> str:
        """
        Measures prefabs as level cards without writing anything (a read-only spot
        check of what generate_asset_catalog(catalog='level') would write), plus the
        footprint computed for cell_size at the card's placementScale: cells at
        rotation 0 ([x, y]), meters, fitError (cells minus meters per axis), height.
        Returns {path: card | reason it is not a level piece}.
        """
        return await call_unity("describe_level_cards", {"paths": paths, "cell_size": cell_size})

    @mcp.tool()
    async def get_asset_catalog_status(
        run_id: Annotated[str | None, "Run id from generate_asset_catalog. Omit for the active/latest run."] = None,
    ) -> str:
        """
        Polls an asset-catalog run: {runId, state (running|done|cancelled|error),
        processed, total, pendingThumbs, plus jsonPath/itemCount/thumbnailCount when done}.
        """
        params: dict = {}
        if run_id is not None:
            params["run_id"] = run_id
        return await call_unity("get_asset_catalog_status", params)

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
            problem = registration.receiving_problem()
            if problem:
                return f"No Unity events recorded in that window. Events may be missing: {problem}"
            return "No Unity events recorded in that window."
        return json.dumps(events, indent=2)
    
    @mcp.tool()
    async def get_lights_affecting_object(
        instance_id: Annotated[int, "The instance_id of the affected GameObject (e.g. the ground not receiving light), not the light itself. Obtain this from get_scene_hierarchy."],
    ) -> str:
        """
        Finds all scene lights and determines if they overlap or illuminate a specific GameObject.
        Distance is measured to the object's renderer/collider bounds, not its pivot, so large
        or flat objects (terrain, ground planes) are judged correctly.

        Returns:
        - All Light components in the scene
        - For each light: name, type, intensity, range, position, lightmapping mode, culling mask, rendering layer mask
        - Distance from each light to the target object
        - Whether the target is within range of each light
        - Total count of lights in range (to spot per-object limit issues)

        Diagnosing a lighting issue (read-only; gather evidence before concluding):
        1. Call this on the affected object. Check in-range count, culling masks vs the object's
           layer, rendering layer masks, and lightmapping mode (Baked lights need a lightmap bake).
        2. Call get_project_settings(sections=["rendering"]) for the URP rendering path,
           per-object light limit, additional-lights mode and shadow distance. In Forward, many in-range lights compete for a few per-object
           slots, so which lights win shifts with camera and position. Symptoms that are
           "inconsistent" or "depend on camera angle" point here. Fixes: raise the limit, reduce
           nearby lights, or switch to Forward+ (no per-object cap).
        3. Call inspect_gameobject with component="MeshRenderer" (or the renderer in use) for
           shadow casting/receiving, light probe usage and materials.
        4. Call get_screenshot(source="scene", focus_instance_id=<id>) to confirm visually.
        Report root cause, evidence, and recommended fixes; say what needs manual review.
        """
        return await call_unity("get_lights_affecting_object", {
            "instanceId": instance_id
        })
