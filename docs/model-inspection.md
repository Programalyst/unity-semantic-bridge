# Imported model inspection

`inspect_model_asset(path, include_details=False)` reads an already imported model
in the connected Unity project. The path must start with `Assets/` or `Packages/`;
FBX, OBJ and other formats handled by `ModelImporter` are supported. Prefabs,
standalone `.asset` meshes, folders and absolute paths return actionable errors.

```python
inspect_model_asset(
    path="Assets/Synty/PolygonPoliceStation/Models/SM_Wep_Bullet_01.fbx",
    include_details=True,
)
```

The compact text report includes:

- All imported Mesh subassets: name, GUID/local file ID, vertex/submesh counts,
  and mesh-local axis-aligned bounds (center and size).
- Imported hierarchy, including inactive nodes, with parent-relative position,
  quaternion rotation (xyzw), scale and sibling-indexed paths.
- Each mesh assignment and its transformed AABB; the union of assigned meshes.
- ModelImporter scale, file-unit and axis-conversion settings.
- Renderer material slots (including null slots), persistent material references,
  embedded materials including unassigned ones, and direct outgoing file dependencies.

`include_details=True` adds submesh topology/index counts, extra importer settings,
other embedded subassets, and recursive rather than direct file dependencies.
Mesh vertices/indices are not dumped or read; Read/Write need not be enabled.

## Coordinate contract

All geometric data is the **imported Unity representation**, not raw FBX coordinates.
Mesh bounds are in each Mesh's own local space. Placement bounds transform the
mesh AABB by the chain of local TRS matrices, **including the imported root TRS**,
into the frame of an imaginary identity parent. They are not scene-instance/world
bounds, and importer scale must not be applied again. The box transform handles
rotations, negative/nonuniform scale and shear; transforming an AABB yields a
conservative box, not necessarily the tight bounds of the transformed vertices.

For skinned meshes this reports undeformed mesh geometry transformed by its node,
not a baked pose or animated renderer bounds. Optimized imports may omit source
hierarchy nodes. Unity's axis-conversion setting is reported, but the source file's
original up-axis metadata is not inferred. Unity's target axes are Y-up, Z-forward,
left-handed.

The implementation only loads imported assets and reads their properties. It does
not instantiate objects, bake meshes, alter selection, change importer settings,
reimport, save assets or modify scenes. It uses public APIs available at the
package's Unity 2022.3 minimum version. Existing instance-based inspectors and the
catalog generator retain their existing contracts.

## Validation

Python registration/routing tests:

```sh
cd mcp-editor-bridge
.venv/bin/python -m unittest discover -s tests -v
```

Unity EditMode: `Gamenami.UnitySemanticBridge.Editor.Tests.ModelInspectionTests`.
Run in a disposable test project with this package in `testables`. Tests create
and delete a uniquely named scratch OBJ/material folder. They cover path errors,
real imported IDs/counts, embedded versus external material remapping, transformed
bounds with nested TRS, and unchanged importer/source/meta/selection/hierarchy/scene
state across inspection. The optional bullet FBX test runs if the Synty fixture
is installed; the asset is not distributed with USB.

### Verified in this change

- Unity 2022.3.62f3: all 10 model inspection tests passed, including the command
  dispatcher and an actual import of a copy of the supplied bullet FBX + `.meta`.
  The bullet has 84 vertices, one submesh, 138 indices and mesh bounds size
  `(0.01222785, 0.04735083, 0.01058963)` in imported Unity units. Read/Write was off.
- All 8 Python bridge tests passed.
- The broader Unity suite passed 25/29. Two pre-existing explicit refresh tests
  timed out waiting for Editor updates in batch mode; two pre-existing
  ScriptableObject tests expected `Assets/__BridgeTests__` to exist. Those
  unrelated runtime/fixture issues were not changed. Test compilation did require
  explicit NUnit/Newtonsoft references and two explicit `long` file-ID out types
  in the existing mesh-reference tests.
- Newer Unity versions and an animated/skinned integration fixture were not run.
  Validation used a disposable project rather than changing the original asset
  project. Python transport was mocked; C# dispatch and imported model inspection
  ran inside Unity. A live restarted MCP client session was not exercised.

After updating, let Unity compile the local package and restart/reconnect the
Python MCP server so clients discover the new tool.

## Separate proposal: incoming references before deletion

The existing `find_asset_references` description claims incoming references, but
`AssetFunctions.FindAssetReferences` calls `AssetDatabase.GetDependencies(path,
false)`. That reports the selected asset's **outgoing direct dependencies**. It
cannot establish whether another asset still references a proposed deletion set.
This implementation does not change that tool's behavior or add deletion logic.

Recommended separate extension: a read-only `check_asset_deletion_references(paths,
scan_roots, ...)` that expands proposed folders into asset files, enumerates assets
outside the deletion set, and intersects their direct dependencies with that set.
Return explicit `referrer -> target` edges, scan scope/progress, completeness,
and errors. Include saved scenes, prefabs, materials, ScriptableObjects and other
asset types; do not filter only by model/prefab types. Package coverage should be
explicit. Large scans should be frame-pumped/cancellable like catalog generation.

This is a scan of Unity's saved/imported dependency graph. Unsaved scene references,
string-based Resources/Addressables/custom loading, and external tools can fall
outside that graph; an empty or incomplete report must not claim deletion is safe.
Correct the old tool's misleading description separately while preserving its
outgoing behavior, or expose an explicitly named outgoing dependency tool with a
compatibility alias.

API references:
- https://docs.unity.cn/2022.3/Documentation/ScriptReference/AssetDatabase.GetDependencies.html
- https://docs.unity.cn/cn/2022.3/ScriptReference/ModelImporter.html
