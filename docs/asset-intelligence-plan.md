# Asset Intelligence for USB: Refined Plan

Refined October 15, 2026 from the draft outline, after a static read of
nuclear-knights (NK), Asset Preview (AP) and this repository. Phase 0 (same day)
re-read the source and ran GridBattle in Play Mode; every §1 item is now marked
**runtime-verified**, **source-verified** (no runtime behaviour to observe) or
**corrected**. The NK-side reference is `nuclear-knights/docs/level-rules.md`, and
the runtime occupancy dump is `nuclear-knights/docs/fixtures/gridbattle-occupancy.json`.

Paths: NK code is under `nuclear-knights/Nuclear Knights/Assets/`, abbreviated
`NK:`. USB package code is under `com.gamenami.unity-semantic-bridge/`, abbreviated
`USB:`.

## 1. What the code says (assumptions confirmed or refuted)

| # | Draft assumption | Verdict | Evidence | Phase 0 status |
|---|---|---|---|---|
| A1 | Obstacle grid positions live in code and feed pathfinding | **Refuted.** Obstacles are discovered from scene physics at runtime | Each `GridTile` carries a 2×4×2 m trigger box (`NK:Prefabs/GridSystem/Grid Tile v2.prefab`, root scale 2,4,2, layer Grid Tiles). `GridTile.OnTriggerEnter` marks the tile occupied when the entering collider's **own GameObject** has a `GridObstacle` (`TryGetComponent`, no parent search); tiles have no Rigidbody, so the obstacle needs one (all GridBattle obstacles use kinematic) and an enabled collider. It then calls `GridManager.SetNodeOccupied`, which sets `PathNode.IsOccupied` | **Runtime-verified.** 24 obstacle cells in GridBattle; an `OverlapBox` with each tile's trigger box and the same filter matched the trigger result on all 2,500 cells |
| A1b | Anything is literal C# coordinates | **Partly.** Player deployment cells are hard-coded | `CharacterLoader._deployableGridPositions = (3,2),(2,3),(0,3)` (`NK:Scripts/GridSystem/CharacterLoader.cs`, `Awake`) | **Runtime-verified.** Reno, Cross and Elena spawned on those cells |
| A2 | The scene holds the visuals; a map SO holds enemies, day/night, etc. | **Confirmed**, with caveats | `Data.LevelInfo` (`NK:Scripts/Data/LevelInfo.cs`) holds `timeOfDay` and `enemiesInLevel` (cell plus facing). Only one exists, `Settings/Levels/Desert 1.asset` (Night, 50×50, eight enemies). Its `gridSize` field is **unused**, because `GridManager` hard-codes 50×50 (`GridWidth`/`GridHeight`). Its CreateAssetMenu label is already "Level Layout", which will collide with the new type's name | **Runtime-verified.** Board was 50×50; all eight enemies spawned on their authored cells |
| A3 | Coordinates: literal source or serialized? | **Neither.** They are geometry | Occupancy is wherever the obstacle colliders happen to overlap tiles, so it is never stored anywhere | **Runtime-verified.** Captured for the first time as the GridBattle fixture |
| A4 | USB constraints | **Confirmed**, plus one more | Requests are single-flight (Python `unity_request_lock`). Instance IDs are unstable. The listener used a **fixed port, 1073**, so only one Editor (AP *or* NK) could serve the bridge at a time | **Corrected.** The port constraint is resolved by per-project ports (NK 1073/1074, AP 1075/1076; commit 3110f44). Phase 0 drove NK on 1073. Also, `invoke_static` is refused during Play Mode, so runtime dumps need an Editor script triggered from `EditorApplication.update` |

Other facts the design depends on (each tagged with its Phase 0 status):

- **Grid geometry.** *Runtime-verified.* The origin is world (0,0) in X/Z, independent of the Grid Manager's X/Z. Cells are 2 m, so cell (x,y) has its center at (2x+1, h, 2y+1), with grid +x along world +X and grid +y along world +Z; h is the Grid Manager's Y (2 in GridBattle), and each tile's trigger box spans Y 0–4 m there. `GridTile.FloorPosition` forces y=0, so the board is **flat**. Nothing in the code models height, stairs, doors, interiors or multiple levels.
- **Movement.** *Source-verified* (no runtime corner test was run; Phase 1 changes the rule). Movement is 8-way, and diagonal steps cost the same AP as straight ones (`GridMap.MOVE_DIAGONAL_COST`). **Corner-cutting is allowed:** a diagonal step between two blocked orthogonal neighbours is legal, because `GetNeighborList` has no corner check and neither `FindPath` nor `GetReachableNodeSteps` adds one. A wall drawn only diagonally therefore does not seal anything. Units occupy nodes too (runtime-verified: all 11 unit cells were `IsOccupied`), and reachability is a BFS over the same neighbours.
- **Cover.** *Runtime-verified* for tile cover; *source-verified* for the waist ray.
  - `CoverType {None, Half, Full}` is set on each `GridObstacle`.
  - A tile's cover comes from the obstacle sitting on each **orthogonal** neighbour (`GridTile.GetTileCoverInfo`). Each tile stores only one obstacle, and the last trigger to fire wins. The fixture records per-side cover for every cell next to an obstacle (e.g. Reno at (3,2) has Half on +x from Wall SF (half)).
  - Cover against a shot (`RangedAccuracy.CoverAgainst`) takes the neighbour side facing the shooter, using the dominant axis (ties go to X). **It then also** casts a ray at 0.5 m height and credits the best `GridObstacle` (parent search) hit within 2.5 m of the target. So the game already honours cover placed *between* cells, which matters for the "illegal states" principle.
  - Units auto-crouch next to Half cover (`GridMovement.HasAdjacentHalfCover`).
- **Line of sight.** *Runtime-verified, with corrections.* LOS uses `Physics.RaycastAll` on the **Default and Stencil layers**, ignoring triggers (`GridVisibility.HasClearSegment`). Units and Half-cover obstacles are ignored. **Every other enabled collider on those layers blocks sight, whether or not it has a `GridObstacle`.** Fog reveal checks the segment from each tile's `VisionCheckPosition` (2 m) to the target's, within a circular range. In GridBattle, every decoration collider is on **Default**; probes across each collider with `HasClearSegment` showed:
  - The sandy rock sets are enabled non-convex MeshColliders without `GridObstacle`. They block sight and overlap a large part of the board (roughly x ≥ 20 and y ≥ 17) without blocking movement. These are the real "sight blocker without obstacle" cases.
  - **Corrected:** Tower_09 (the windmill prop's support column) and Tower_10 (a four-legged tower that looks walkable underneath) are **inactive GameObjects** in GridBattle, so they block neither sight nor movement. Their prefab colliders are enabled, and Tower_10's would block sight and, as an obstacle, movement under its legs. That makes it a catalog case where the bounding box overstates the ground footprint.
  - The windmill is Half cover, so although it is a tall prop it does not block sight.
- **The only hand-made map is a prototype.** *Runtime-verified, with corrections.* GridBattle has five `GridObstacle` instances under an "Obstacles" parent, but only four occupy cells (24 in total):
  - Wall SF (Full, scale 1.65, 2, 1.5 in the scene) at (9, 10) rotated 90° covers (4,4) and (4,5).
  - Wall SF (half) (Half) covers (4,2) and (4,3).
  - The Crystal Patch (Half, 7×7 m box) covers x 10–13, y 4–7.
  - The Windmill is `SM_Bld_WindMill_01 Variant` from **PolygonSciFiWorlds** (scale 0.5), not the Wasteland prop. It is **Half** cover, at (30, 17.5) rotated 225°, and covers (14,8), (15,8), (14,9) and (15,9).
  - **Corrected:** the Refinery Pump (a `Refinery Pump 01 Variant` with an enabled collider, kinematic Rigidbody and `GridObstacle`) is an **inactive GameObject** in GridBattle, so it occupies nothing.
  - The undecorated scenery is Tower_09/10 and the Wasteland `SM_Prop_Windmill_01` (all inactive), the APCs (out of bounds) and the sandy rock sets. The rocks are distant visual blockers meant to discourage leaving the play area, but they have no `GridObstacle` and sit inside the 50×50 board, so units can path through them. D4's smaller `gridSize` is the cleaner boundary. That gives a very thin calibration set.
- **The test style is menu-item harnesses, not NUnit.** *Source-verified.* NK has no asmdefs; everything compiles into Assembly-CSharp. `NK:Scripts/GridSystem/Editor/*Validation.cs` are `[MenuItem("Tools/Grid Combat/Validate …")]` statics that throw `*_CHECK_FAILED` or log `*_CHECKS_PASSED`. `GridPathValidation` already exercises `GridMap` without a scene. They already run through `invoke_menu_item`/`invoke_static`.
- **Pathing is already nearly pure C#.** *Source-verified.* `GridMap`/`PathNode` depend only on `Mathf`/`Debug`. The scene-coupled parts are occupancy (triggers), cover (`GridTile`) and LOS (physics).
- **An asset catalog already exists, but it is character-oriented.** *Not rechecked in Phase 0* (AP-side; Phase 4 will revisit it).
  - `USB:Editor/AssetCatalogGenerator.cs` (v4) produced `Asset Preview/AssetCatalog/asset-catalog.json`: 11,337 items, including 5,249 prefabs from PolygonApocalypse, ApocalypseWasteland, Generic and SciFiWorlds, plus 11.3k thumbnails.
  - The records have **no bounds, footprint or pivot data**.
  - `InferCategory` only knows hair, facegear, outfit and weapon, so 4,370 prefabs are just "prop".
  - An `overrides` overlay already exists (`LoadOverrides`).
- **Packs.** *Runtime-verified for NK, with corrections.*
  - The Wasteland pack is mostly whole buildings (Shack, Building, Tower, Garage). It also has wall chunks, fences, barriers, tyre barriers and sandbags (AP; not rechecked).
  - The **modular road, sidewalk and crossing kit is in PolygonApocalypse**, which exists only in AP, not in NK.
  - **Corrected:** NK's 22 MB `3rd Party/PolygonApocalypseWasteland` subset (694 MB in AP) holds only `SM_Prop_Barrier_01`–`08`, `SM_Bld_Tower_09`/`10`, `SM_Prop_Windmill_01`, a skydome and a few character attachments. It has **no wall chunks, sandbags, fences or tyre barriers**, so those come from AP via the D1 export.
  - Measured sizes (root scale applied; full numbers in `level-rules.md`): Wasteland barriers are 3.22–3.27 m long (Barrier_04 is 2.10 m), 1.11–1.46 m high and 0.69–0.92 m deep, with MeshColliders and no `GridObstacle`. Tower_09 is 1.6 × 7.2 × 1.6 m and Tower_10 is 5.1 × 6.4 × 4.6 m. None of these divide evenly into 2 m cells: a 3.26 m barrier spans two cells with about 0.7 m of overhang. The 2.5 m/5 m modular snap still needs checking on AP's wall and road kit in Phase 4. **Approach decided (2026-10-15, after Phase 2):** each piece's scale is settled once, in the Phase 4 gallery review, and stored on its card as `placementScale`; layouts reuse it (see §3 *Piece scale*).
  - NK's own walls are scaled prefab variants. Wall SF's prefab scale is (1.65, 2.5, 2), giving a 3.91 × 3.79 × 1.79 m visual and a 3.91 × 3.63 × 1.0 m collider. Wall SF (half) is (1.65, 0.75, 1), giving a 3.91 × 1.14 × 0.90 m visual and a 3.80 × 1.05 × 0.70 m collider. GridBattle overrides both scales. The visual is thicker than the collider, so cards need both bounds.

## 2. What changes in the approach

1. **Occupancy, not pathfinding, is the core refactor.** Pathfinding is already isolated and tested. What isn't deterministic or computable at edit time is *which cells are blocked and what cover they give*. The first engineering step is an **edit-time occupancy baker** that reproduces the trigger rule with `Physics.OverlapBox` using the same 2×4×2 box. Phase 0 already showed that this `OverlapBox` rule matches the Play Mode trigger result on all 2,500 GridBattle cells; Phase 1's harness turns that into a repeatable check against the fixture. That gives validators the game's real rules without touching gameplay code.
2. **The scene stays the authoring source; JSON is the agent's draft format; runtime reads a bake.** (Decision D3.)
   - You keep laying levels out in the scene. The agent drafts in compact JSON and `apply_layout` turns the draft into scene objects. `bake_layout` reads any scene (hand-made or applied) back into the same JSON, so both directions work and the scene always wins.
   - **Now:** keep the runtime trigger system. Apply fits each blocking piece's collider *inside* its footprint cells (inset ~0.1 m), so the triggers reproduce the layout exactly. A dev-only start-of-battle assertion checks that runtime occupancy matches the bake.
   - **Later, for larger maps:** `GridManager` loads a baked occupancy asset per level (a "grid bake", like a NavMesh bake) instead of waiting for trigger callbacks. The triggers stay as the cross-check, then retire. A freshness check (a hash of the obstacle transforms) flags a stale bake.
3. **Flat board; 2–3 storey buildings as solid blockers; no interiors for now.** (Decision D5.) There are no stairs and no doors as passages, and the upper floors can't be reached. Doors are only a *facing* hint for plausibility. The layout records a building's `storeys` so that sight blocking and the later elevation work have the data. The game has no height advantage yet, so neither do the metrics.
4. **Fix corner-cutting in the game; validators use the fixed rule.** (Decision D2.) A diagonal step is legal only when neither orthogonal neighbour holds an obstacle. Units do not restrict diagonals, which needs a separate `PathNode.HasObstacle` flag because today `IsOccupied` covers both units and obstacles. Walls then seal with orthogonal or diagonal continuity, which simplifies the "sealed pocket" checks.
5. **Two LOS tiers.**
   - A *grid tier* (supercover line over blocking cells, ignoring Half) is fast and pure, and is used for metrics during generation.
   - A *physics tier* runs after apply with `GridVisibility.HasClearSegment` itself, so it is the game's real rule. It catches decoration that blocks sight but isn't an obstacle.
   - A consistency validator reports any disagreement between the two.
6. **Make deployment data, not code.** Move `_deployableGridPositions` into `LevelInfo` (default = current values) so reachability, spawn distance and fairness can see both sides.
7. **The catalog is built in AP; levels are built in NK.** The workflow is: generate or extend the catalog in AP (PolygonApocalypse plus PolygonApocalypseWasteland, decision D1), then move the chosen pieces to NK with a GUID-preserving `.unitypackage` export, then build in NK. The catalog records GUIDs, so layouts written against AP's catalog resolve in NK after import. Per-project bridge ports (USB prerequisite, §4) let both Editors stay connected.
8. **`LevelInfo.gridSize` drives the board.** (Decision D4.) `GridManager` currently hard-codes 50×50 and gets no `LevelInfo`. It needs the level's size before `InitGridTiles` runs in `Awake`, so the `LevelInfo` reference moves to where both `GridManager` and `CharacterLoader` can read it (e.g. `GridManager` owns it and `CharacterLoader` reads it from there). Fog bounds (`FogGridBounds`) and `GridFogOfWarMask` follow the same size.

## 3. Design decisions

- **Layout shape.** Integer cells covering the whole board (`LevelInfo.gridSize`). There are four rotations (0–3 quarter turns). Pieces are anchored at their min corner, with the footprint taken from the catalog card after rotation. Per cell, the derived state is `{blocked, cover: None|Half|Full, occupant pieceId}`, with at most one blocking occupant per cell, which matches the game's one-obstacle-per-tile rule. Non-blocking decoration and ground pieces (roads, sidewalks) go in a separate layer that never blocks. There are no edge-based concepts: the game has none, and cell-based cover makes "between tiles" unrepresentable. *Planned (Phase W, D12):* the one exception is walls on cell sides for enterable buildings: per cell and side, the cover a wall gives and whether movement may cross it, with footprint cells walkable.
- **Storage.** The scene is the source of truth. The JSON layout is the agent's draft and interchange format (written by the agent or produced by `bake_layout`), e.g. `NK:Settings/Levels/<name>.layout.json`. The agent writes the file directly and calls `apply_layout`, which avoids field-by-field SO edits. It is diffable and token-cheap. A compact form:
  ```json
  {"v":1,"size":[12,12],
   "pieces":[["wallF","5fe5…",3,4,1],["sandbag","…",6,2,0]],
   "deploy":[[1,1],[2,1],[1,2]],
   "decor":[["road_straight","…",0,5,0]]}
  ```
  Each piece is `[localId, prefabGuid, x, y, rot]`. Reports address cells as `x,y`. Enemy spawns join the format in Phase 3 (D9), e.g. `"enemies":[["raider1","<profileGuid>","<visualGuid>",10,11,180]]` (localId, unit profile and visual asset GUIDs, cell, facing in degrees); the adapter maps them to the game's level data.
- **Genericity.** USB owns the catalog, the layout schema, compile, render, and a validator runner. The validator runner covers bounds, overlap, footprint, the reachability engine, the metrics framework and report formatting. The game supplies an adapter, an `IGridRulesAdapter` interface defined in **`UnitySemanticBridge.Runtime`**. That assembly is `autoReferenced`, so Assembly-CSharp can implement it without NK adding asmdefs. USB Editor finds implementations through `TypeCache`. The adapter exposes:
  - cell size, board size and world↔cell conversion
  - the neighbour rule (8-way, no corner-cutting past obstacles)
  - the cover rule (orthogonal neighbours)
  - LOS layers and ignore rules
  - how to bake occupancy from a scene
  - a piece-to-component hook, e.g. adding `GridObstacle` with `coverType` and meshes
- **Where code lives.**
  - Pure rules (grid, neighbours, BFS, grid LOS, metrics) go in plain C# with no `UnityEngine` (own `Cell` struct) in USB `Runtime/Layout/`.
  - NK's adapter and baker go in `NK:Scripts/GridSystem/Layout/`, with Editor parts in `…/Layout/Editor/`.
- **Catalog cards.** Extend `BuildPrefabRecord` with fields measured on `PrefabUtility.LoadPrefabContents` (isolated, never in the open scene). Every renderer's `Mesh.bounds` corners and every collider's bounds are transformed into root space (`root.worldToLocalMatrix * child.localToWorldMatrix`), with the root at scale 1. Child scales count; the root's scale and placement don't. Each card holds:
  - `visualBounds` and `colliderBounds` (meters at scale 1; `colliderBounds` is null when the prefab has none), `pivotOffset`, `height`, `storeys` (buildings)
  - a `skewed` flag when a child has a non-90° rotation, because the box then overstates the size
  - `levelRole`: `blocker|cover_half|cover_full|ground|decor|building|unknown`
  - `placementScale` (the approved root scale for layouts; defaults to the prefab root's own scale until reviewed) and `rootRotation` (layout rotations are quarter turns on top of it)

  Footprint cells and fit error are **computed on request** from the card, the cell size and its `placementScale` (or a per-piece exception), not stored. One card then serves any cell size or scale.

  Detection order is overrides file, then name/folder heuristics (`Wall`, `Barrier`, `Sandbag`, `Fence`, `Road`, `Sidewalk`, `Bld_`), then geometry (height < 1.2 m → half cover, ≥ 2 m → full), then vision on the thumbnail only for leftover `unknown` entries. Cards go into a separate `level-catalog.json` keyed by GUID so the 12 MB character catalog doesn't grow, and are filtered to level roles.
- **Piece scale.** Settle each piece's scale once, at catalog time, and make every layout reuse it; adjusting scale per level, by inspecting each generated scene, does not scale. Phase 2 showed why: apply places prefabs at their authored scale, and NK's walls came out taller and lower than the owner's GridBattle walls, whose real scale lived only in scene overrides.
  - **Gallery review.** "Gallery" means a disposable, generated NK scene (not an Editor window or a separate tool): a small script lays the candidates out in a row on the grid, each beside a soldier for scale, built with the same apply machinery as `LayoutTest`. The agent captures it top-down and from the side (`get_screenshot`, later `render_layout_topdown`), and the owner reviews the captures or opens the scene in the normal Scene view. The owner approves or adjusts it once; the result goes into the overrides file as `placementScale`.
  - **Apply and bake use the card.** Apply instantiates at `placementScale` and measures footprints at that scale; bake accepts instances whose scale equals their card's and reports anything else (today: the prefab's scale).
  - **Heights follow the rules.** Height decides cover and sight, so `level-rules.md` gets a target range per role, e.g. half cover about 0.9–1.2 m (shoot over it crouched), full cover 2 m or more (blocks sight). The Phase 3 scale validator flags pieces outside their role's range, and the agent proposes a scale instead of the owner spotting it.
  - **What may be scaled how.** Props scale uniformly to a sensible cell fit. Wall and other modular runs may scale per axis: length to a whole number of cells, height to the cover class. Buildings keep their pack scale unless they look wrong next to a soldier.
  - **Hand-made scenes are the reference.** Instance scales the owner chose in hand-made scenes (GridBattle's walls) are recorded preferences; the agent reads them and seeds card overrides from them, as Phase 2 did for Wall SF.
  - **Per-piece scale in the layout JSON is a rare exception** (an optional sixth field for a one-off feature such as a large rock), not the default, so a piece looks the same in every level and drafts stay small.
  - **Feedback loop.** A request like "walls too tall" becomes a card override change plus a reapply of the levels that use the piece. Hand edits to generated objects are lost on the next apply; the bake already reports them as off-scale, and a later convenience can offer to copy such a tweak onto the card.
- **Sockets.** Reduced to a building `front` direction (door side) and wall-run `ends`. The front is the model's +Z (turned by its root rotation), else the override file; door-child name heuristics were tried in 4d and dropped (Synty buildings have doors front and back). Ends: name heuristics or the override file. They matter only for the plausibility checks.
- **Token efficiency.** Validator reports are capped lists of `code x,y message` lines, with ASCII map dumps (`#` full, `h` half, `.` open, `A/E` spawns, `?` unreachable) on request only.

## 4. Phases (reordered) and tasks

**Prerequisite (USB): per-project bridge ports.** Done 2026-10-15 (commit 3110f44); NK on 1073/1074 and AP on 1075/1076 were verified side by side.
- [x] Store the Unity listener port (default 1073) and the Python event port (default 1074) per project in `UserSettings/`. Show them in the bridge window, and give a clear error when a port is already in use. Make the auto-connect flag per project too (it is in machine-wide `EditorPrefs` today).
- [x] The Python server takes `--unity-port`/`--event-port` (or environment variables), with today's defaults. Update `docs/installation.md` and the tests that assume 1073.
- [x] Register a second MCP server for AP. Done 2026-10-15 in the local agent configs (Claude desktop and Codex), which are not in any repo. NK keeps the existing `unity-semantic-bridge` entry on the default ports 1073/1074. `unity-ap` passes `--unity-port 1075 --event-port 1076` and has the same Codex tool approvals.

**Phase 0: Recon close-out (read-only, small).**
- [x] Write `nuclear-knights/docs/level-rules.md` from §1 (grid, movement, cover, LOS, scene conventions, LevelInfo), and link it from `docs/README.md` reading order.
- [x] *Runtime checks* in GridBattle: dump runtime occupancy and cover per cell via a temporary `invoke_static`; LOS-block test against the Tower_09/10 colliders; layers of the decoration colliders; real bounds of 5–10 candidate pieces from both packs via `inspect_model_asset`.
- Exit: every §1 row is marked runtime-verified, and the occupancy dump is saved as a fixture. **Done 2026-10-15.** The fixture is `nuclear-knights/docs/fixtures/gridbattle-occupancy.json`. The temporary probe was removed, and GridBattle and its assets are unchanged.
- Findings for later phases:
  - The baker must use `TryGetComponent` on the collider's own GameObject and skip inactive objects and disabled colliders.
  - The consistency validator will flag the GridBattle rock sets as sight blockers without obstacles.
  - Sandbags and wall chunks must come from AP.

**Phase 1: Edit-time occupancy baker plus the game rule changes.**
- [x] `NK:Scripts/GridSystem/Layout/Editor/OccupancyBaker.cs`: an OverlapBox per cell with the tile trigger's box, the `GridObstacle` filter and the last-wins rule.
- [x] `LayoutValidation.cs` harness (`Tools/Grid Combat/Validate Layout Baker`): baked == Phase 0 runtime dump for GridBattle, plus synthetic fixtures (sliver overlap blocks the neighbour, boundary-straddling 2-cell wall, rotated building).
- [x] **Corner-cutting fix:** `PathNode.HasObstacle` set by `GridTile.SetOccupied(GridObstacle)`; `GridMap` skips a diagonal when either orthogonal neighbour has an obstacle (in `FindPath` and `GetReachableNodeSteps`). Update `GridPathValidation`'s independent BFS to the same rule, and add wall-corner cases. Re-run the AP, firing-position and bug-range harnesses, since paths near walls get longer.
- [x] **Board size from `LevelInfo.gridSize`:** `GridManager` reads the level before `InitGridTiles`; `CharacterLoader` takes the level from it. Fog mask and bounds follow. Desert 1 already says 50×50, so GridBattle is unchanged.
- [x] **Deploy cells in `LevelInfo`:** a `deployCells` list (Desert 1 gets (3,2),(2,3),(0,3)); `CharacterLoader` reads it. Rename the CreateAssetMenu label to "Level Info".
- [x] Update `docs/combat-flows.md` (deployment, movement) and `level-rules.md` in the same change.
- Exit: all harnesses pass via `invoke_menu_item`, and GridBattle plays as before apart from the corner rule. **Done 2026-10-15** (NK ababe3fb). Validate Layout Baker (21 checks; GridBattle bake equals the fixture on all 2,500 cells), Paths (1,464), Action Points (66), Enemy Firing Positions (21) and Bug Attack Range (20) passed via `invoke_menu_item`. In GridBattle Play Mode the 11 units spawned on their Phase 0 cells, the path map had the same 24 obstacle nodes, and Reno's move from (3,2) to (5,1) went (3,2) → (3,1) → (4,1) around the low wall's end instead of cutting past (4,2).
- Notes for later phases:
  - The `LevelInfo` reference moved from `CharacterLoader` to `GridManager` in GridBattle (one serialized reference; no authored content changed).
  - The baker sorts overlapping obstacles by hierarchy path for a deterministic "last wins", reports each such cell (`MULTI`), and reports obstacles without a Rigidbody (`NO_RIGIDBODY`). It also skips colliders on layers that ignore Grid Tiles.
  - `PathNode.IsObstructed` was renamed `IsOccupied` after Phase 1 (NK d14e1ff0); this plan uses the new name throughout.
  - The AP, firing-position and bug-range harnesses first built their walls with `IsOccupied` only, so they didn't exercise the corner rule. A follow-up marks those walls as obstacles too (NK 34a6ee81); the AP wall detour now costs six tiles instead of four.

**Phase 2: Layout model, apply, bake and the test scene.**
- [x] USB `Runtime/Layout/`: schema, parser, pure grid model, and `IGridRulesAdapter`.
- [x] NK adapter `NkGridRules.cs`.
- [x] USB Editor `LayoutApplier`: instantiates by GUID under a `[Generated Layout]` root, applies rotation and pivot offset, fits colliders inside footprints, calls the adapter's piece hook, and makes it all one Undo group. Reapplying clears the root only.
- [x] `bake_layout`: reads a scene's obstacles (hand-made or applied) back into layout JSON. Off-grid items are reported, not silently snapped.
- [x] **New test scene `LayoutTest.unity`** (decision D6), with its own `LevelInfo`: a copy of GridBattle's managers, cameras and UI, and an empty board. GridBattle itself is left alone.
- Exit: apply → bake round-trips, and a Play Mode assertion in `LayoutTest` confirms runtime occupancy equals the layout. **Done 2026-10-15** (USB e809fcc, NK 9dae1d2d). How it works, with a USB-versus-game flow diagram: [grid-layouts.md](grid-layouts.md). USB: 21 `LayoutModelTests` (NUnit, `TestProject`) and the `bake_layout` MCP tool. NK: **Validate Layout Round Trip** (2,042 checks: USB's neighbour rule equals `GridMap` on 25 seeded maps; `LayoutTest.layout.json` applies and bakes back identically, with the game's occupancy bake equal to the layout on every cell including cover; reapply/Undo; off-grid walls reported) and Validate Layout Baker (21) passed via `invoke_menu_item`. In `LayoutTest` Play Mode a temporary probe (since removed) found runtime `HasObstacle`, tile obstacle cover and per-side cover equal to the layout on all 144 cells (26 obstacle cells).
- Notes for later phases:
  - **Footprints** are measured on the instance (blocking-collider bounds in the piece's quarter-turn frame, all scales applied, rounded up to whole cells after a 5 cm tolerance) until Phase 4 cards exist. The first pieces are NK's Wall SF, Wall SF (half) and the Grid Crystal Patch.
  - **Rotation is relative to the prefab root's own rotation**: rot 0 places the prefab as authored. Wall SF's root is already turned 90°, so at rot 0 it runs along grid y. Cards should record the root rotation too.
  - The adapter is pure data plus hooks: `GridRules` (cell size, 8-way, no corner-cutting, orthogonal cover) feeds the pure `LayoutGrid`; `TryGetBoard`, `CellToWorld`/`WorldToCell`, `SightLayers`/`IgnoredBySight`, `IsBlockingCollider`, `Classify`, `DescribeCover`, `ConfigurePiece`, `BakeOccupancy` and deploy read/write cover the rest. `SightLayers`/`IgnoredBySight` are declared but unused until the Phase 3 physics tier (now used by the consistency check).
  - NK's `OccupancyBaker` moved out of `Editor/` (same GUID) because `NkGridRules` lives in Assembly-CSharp. `GridObstacle.Configure` was added for the compile hook on prefabs without a `GridObstacle` (cover from height: below 1.2 m Half, else Full).
  - Apply fits only axis-aligned `BoxCollider`s; other blocking colliders that reach past their footprint are reported (`FIT_UNSUPPORTED`). It does not yet check that a collider touches every footprint cell; the round-trip occupancy diff catches that.
  - `apply_layout` is an Editor API (`LayoutApplier.Apply`) for now; the MCP tool is Phase 5. `bake_layout` is already an MCP tool (optional `path` writes the JSON).
  - A GridBattle bake keeps only the Grid Crystal Patch (10,4) and reports the two walls (scene scale ≠ prefab), the windmill (0.5 m off-grid) and the scaled rock sets, with their eight blocked cells as occupancy differences. That is the expected "report, don't snap" behaviour; Phase 3's GridBattle fixture should account for it.
  - D5 is still unconfirmed; Phase 2 didn't depend on it.
  - Pieces use the prefab's own scale. After testing LayoutTest, the owner found the applied walls too tall/low next to GridBattle, so NK's Wall SF prefab became (1.65, 2, 1.5) and Wall SF (half) (1.65, 0.8, 1), GridBattle's instance values (GridBattle overrides them, so it is unchanged). §1's prefab measurements predate this. From Phase 4 on, scale comes from the cards' `placementScale` (§3 *Piece scale*). NK's **Tools → Grid Combat → Apply LayoutTest Layout** reapplies the test scene from its JSON.

**Phase 3: Validators.**
- [x] **Enemy spawns in the layout JSON** (D9): an `enemies` section, parsed and serialized like the rest; apply writes the spawns into the level through the adapter (NK: `LevelInfo.enemiesInLevel`) and bake reads them back, exactly like deploy cells. Adapter methods `ReadEnemySpawns`/`WriteEnemySpawns`; the profile and visual GUIDs are the game's asset references.
- [x] Structural: bounds, overlap, footprint fit error, one blocker per cell, every piece on integer cells.
- [x] Consistency: every blocking cell has a collider, and every Default/Stencil collider inside the board is either a layout blocker or flagged as a "sight blocker without obstacle".
- [x] Reachability: BFS with the game's (fixed) neighbour rule from deploy cells, covering all enemy spawns, sealed pockets and unreachable open cells.
- [x] Encounter: spawns not on blocked cells, spawns inside bounds, facing sanity.
- [ ] Plausibility: road continuity in the ground layer, building fronts facing open or road cells. *Moved to Phase 4d* (needs catalog roles); done there.
- [ ] Scale: every placed piece uses its card's `placementScale` (or a declared per-piece exception), and its height falls in its role's range from `level-rules.md` (§3 *Piece scale*); report the measured height and a suggested scale. *Moved to Phase 4d* (needs cards); done there.
- [x] Fixtures: a bake of GridBattle (should pass; the Windmill is on the playable grid by design) plus deliberately broken JSON fixtures.
- [x] Tests: the pure validators in USB `Runtime/Layout/` get NUnit tests in `USB:Tests/Editor/`, run through `TestProject`. NK-side checks stay menu-item harnesses (decision D8).
- Exit: cell-addressed reports, and all fixtures behave as expected. **Done 2026-10-16** (plausibility and scale moved to Phase 4d, as scoped). How the validators work: [grid-layouts.md](grid-layouts.md#validators). USB: `LayoutValidator`/`LayoutReport` (pure) and `LayoutSceneValidator` (Editor, physics tier); 49 NUnit tests (`LayoutModelTests`, `LayoutValidatorTests` with 17 broken-layout fixtures) pass. NK: **Validate Layout Reports** (37 checks: applied LayoutTest gives a clean report; four broken JSON fixtures in `docs/fixtures/layouts/` report exactly their expected lines; GridBattle's report equals `docs/fixtures/gridbattle-layout-report.txt`), **Validate Layout Round Trip** (2,049, now with enemy spawns and a refused-spawn revert) and Validate Layout Baker (21) passed via `invoke_menu_item`. A live `bake_layout` of LayoutTest equals its JSON with no issues. In LayoutTest Play Mode a temporary probe (since removed) found 26 obstacle cells and cover equal to the layout on all 144 cells, and both raiders on their cells with their facings.
- Notes for later phases:
  - Reports are `CODE x,y message` lines with Error/Warning severity, errors first, capped at 40 lines by default, with an optional ASCII map (`A` deploy, `E` enemy, `-` unreachable). Warnings: `POCKET`, `FACING_OFF_AXIS`, `SIGHT_NO_OBSTACLE`, `DECOR_OUT_OF_BOUNDS`.
  - Levels store no spawn ids, so a bake names spawns `enemy1`, `enemy2`, … and `LayoutDocument.Diff` compares spawns by order, ignoring ids. Facing is a number in the JSON (no snapping); the encounter check wants 0–359 and warns off the eight grid directions.
  - For a hand-made scene, encounter and reachability run on the game's occupancy bake, since unrepresentable pieces still block. For an applied draft they run on the draft's footprints, and consistency compares those with the game's bake.
  - Sight colliders are found with an overlap box per cell from 0.5 to 2.5 m above the floor, grouped by prefab instance; a group is an obstacle when it holds a blocking collider or classifies as a blocker.
  - **Correction to the Phase 2 note:** since the Wall SF prefabs took GridBattle's scale, a GridBattle bake keeps its two walls as pieces. The report now lists the windmill (`OFFGRID_POSITION`, four `EXTRA_BLOCK` cells), the two rock sets (`OFFGRID_SCALE`, 2×) and the rock sets as `SIGHT_NO_OBSTACLE` (216 and 395 cells); every Desert 1 enemy is reachable.
  - A apply that throws after touching the scene (e.g. the adapter refusing a spawn GUID of the wrong type) now destroys its new root and reverts its Undo group.
  - The temporary-scene setup for NK harnesses is shared (`LayoutHarnessScene`).

**Phase 4: Level catalog (runs in AP; can start alongside Phase 1).** Split on 2026-10-16 into three sessions plus a follow-up, because it spans a third project and two steps wait on the owner. Each part has its own exit.

*4a: Catalog code (USB, run in AP; no owner review needed).*
- [x] Extend `AssetCatalogGenerator`/`AssetCatalogRunner` with the card fields (§3 *Catalog cards*: renderer and collider bounds in root space, root rotation, role, cover, `placementScale`) and the `level-catalog.json` output, reusing the batching and thumbnail pumping. Tests go in `USB:Tests/Editor/`.
- [x] Role detection in the order of §3: overrides file, then name/folder heuristics, then geometry. Define the overrides file format (role, cover, `placementScale`, per-piece exceptions).
- Exit: the generator writes cards for a sample of AP prefabs, footprints computed on request match hand measurements (Wall SF, a Wasteland barrier), and the tests pass. **Done 2026-10-16.** How it works: [level-catalog.md](level-catalog.md). `generate_asset_catalog(catalog="level")` wrote 983 cards with thumbnails for AP's Wasteland `Prefabs/Props` and Apocalypse `Prefabs/Buildings`. Footprints match `level-rules.md`: NK's Wall SF 3.91 × 2.90 × 0.75 m → 1 × 2 cells at rot 0, Wall SF (half) 3.80 × 1.12 × 0.70 m, and SM_Prop_Barrier_01 (visual 3.26 × 1.45 × 0.79 m) → 2 × 1 with 0.69 m slack. Wall SF was checked in NK with the read-only `describe_level_cards`. 53 new NUnit tests (`LevelCardTests`, `LevelCatalogGeneratorTests`) and the 49 layout tests pass (102).
- Notes for 4b/4c:
  - Footprint math is pure (`LevelCards.Footprint` in `Runtime/Layout/LevelCard.cs`) and returns the rot-0 `PieceSpec` size, the bounds centre the applier subtracts, the fit error and an `Approximate` flag, so 4c can replace `PieceGeometry`'s instance measurement with it.
  - Additions beyond §3: a `cover` field (null for `blocker`/`unknown`, where the adapter decides), `roleSource` and `scaleSource` for review, decor name tokens (`Greeble`, `Decal`, `Sign`) checked first, and the geometry rules "longest side < 0.5 m → decor" and "< 0.35 m tall → ground". `blocker` means a cover-family name between 1.2 and 2 m (e.g. Barrier_01 at 1.48 m), and `unknown` an unnamed piece in that band.
  - Card ids drop `Assets/`, `3rd Party/` and `Synty/`, so a prefab has the same id (and GUID) in AP and in NK's `Assets/3rd Party/…`; override globs are portable.
  - Known misfires for the review: `Bld_Door_*` comes out `cover_full`, bunker ceiling tiles `ground`, and some beams `decor` or `cover_full`. Fix them in `LevelCatalogOverrides.json`.

*4b: Scan, shortlist, review in AP, import approved pieces (AP + NK).* Workflow agreed with the owner on 2026-10-17: the agent does the scanning, shortlisting and every import; the owner reviews once, in AP (next to Synty's own showcase scenes), so only approved pieces reach NK and nothing needs deleting there.
- [x] Scan both PolygonApocalypse and PolygonApocalypseWasteland in AP (`generate_asset_catalog(catalog="level")`). *2026-10-17:* 3,516 prefabs → 2,787 cards with thumbnails; before overrides 25 blocker, 197 building, 527 cover_full, 565 cover_half, 616 decor, 629 ground, 228 unknown.
- [x] **Agent shortlist:** about 80–100 candidates from the cards and thumbnails, including the road/sidewalk kit, walls, sandbags and other cover, and 2–3 storey buildings. Fix obvious role misfires (doors, ceilings, beams) in the overrides file. The agent may capture AP views to check its own picks.
- [x] **Scale reference in AP:** import NK's soldier (one unit visual prefab and its dependencies, GUID-preserving) into an AP folder such as `Assets/_NK Reference/`, so the gallery compares pieces with the real unit size. Read-only use; nothing flows back.
- [x] **Gallery in AP** (§3 *Piece scale*): a generated, disposable scene (no new Editor UI) with each candidate on a 2 m cell grid at its suggested scale beside the soldier, grouped by role, captured top-down and from the side. Seed suggested scales from the role height targets (e.g. half cover about 0.9–1.2 m, full cover 2 m or more) and from the owner's GridBattle instances (Wall SF). Rescaling means editing the overrides file and regenerating the scene.
- 4b results so far (2026-10-17): 103 candidates (26 ground, 31 half cover, 27 full cover, 19 buildings) in NK `docs/level-piece-review.md`, with a proposed role and `placementScale` each, written to AP's `AssetCatalog/LevelCatalogOverrides.json` as notes marked "proposed". Proposals: road/sidewalk kit at 0.8 (5 m tiles → 2 × 2 cells), runs stretched to whole cells and to 0.9–1.2 m (half) or ≥ 2 m (full), props uniform, buildings at pack scale. Role fixes for the whole catalog by rule: doors, bunker ceilings/beams, guard rails, bollards, wall-mounted bits, fence posts and hanging tyres → decor; `SM_Veh_Road*` → cover_full. Scale reference: NK's `Cross_Junker_Male_01.prefab` copied into AP `Assets/_NK Reference/` with its GUID (its SciFiSpace mesh, materials and textures were already in AP with the same GUIDs, so only the prefab was needed). Gallery: AP `Assets/_LevelGallery/LevelGallery.unity`, built by `Assets/_LevelGallery/Editor/LevelGalleryGenerator.cs` (AP-only: it reads `AssetCatalog/level-shortlist.json` and the cards, so a rescale is an overrides edit, a catalog re-run and `Generate`). Found: heights are top-above-pivot and apply puts the pivot on the floor. Three Wasteland barricades (Barricade_01/08/12) are pivoted at mid-height (mesh −0.9 to +0.9 m), so they sink half into the floor and their card height (about 0.93 m) is half their real 1.85 m; they get a per-piece `lift` override (pivot height above the floor, added to the card and its `height`; USB `LevelCard.Lift`, docs in `level-catalog.md`). Other pieces only have 0.1–0.9 m of foundation below the pivot, meant to sink.
- [x] **Owner review in AP:** approve, reject or rescale per piece. Approved roles and scales go into the overrides file as `role`/`placementScale`.
- [x] **Import to NK (agent):** export only the approved pieces as one GUID-preserving `.unitypackage` into NK `Assets/3rd Party/…` (moved here from 4c). Run the level generator (or `describe_level_cards`) in NK on them; ids and GUIDs match AP.
- [x] **Overrides home:** `LevelCatalogOverrides.json` is kept in the NK repo (canonical, committed) and copied into AP's `AssetCatalog/` before each AP scan; AP is not under version control.
- [x] Add the confirmed role height ranges to NK `level-rules.md`. *2026-10-17:* owner decided half cover 0.9–1.2 m, nothing between 1.2 and 1.6 m, full cover 1.6 m or more (Barricade_01 approved as full at 1.68 m); NK `level-rules.md` § *Piece heights by role* (uncommitted until the 4b NK commit).
- 4b results (2026-10-17, approved by the owner the same day; nothing committed yet): 101 pieces approved (#42 Wall_Brick_Collasped_01 rejected, one cover per piece; #50 Barricade_08 reclassified as decor, a wall decoration). Review outcomes: height bands half 0.9–1.2 m, none 1.2–1.6 m, full ≥ 1.6 m; five movement-only blockers (role `blocker`, cover `None`: the three Wasteland tyre barriers, Tire_Pile_01, Roadblock_01), for which NK's `GridVisibility` and `NkGridRules.IgnoredBySight` now let sight through cover-None obstacles (Validate Layout Reports 37/37); two new override fields in USB, `lift` (pivot height above the floor; three Wasteland barricades are pivoted at mid-height) and `footprint` (meters claimed on the grid, so a piece may overhang its cells; Tyre_Pile_02 claims 1 × 1), with 5 new NUnit cases (`LevelCardTests` + `LevelCatalogGeneratorTests` 58/58); the gallery lifts ground pieces 0.25 m. The owner rescaled five pieces in the gallery and applied four of them to the AP prefab roots, which went to NK with the prefabs. Export: `Assets/_LevelGallery/Editor/LevelPieceExport.cs` in AP wrote `AssetCatalog/export/nk-level-pieces-4b.unitypackage` (308 assets); the 289 not already in NK were copied into NK `Assets/3rd Party/<pack>/…` with their `.meta` files (19 already present by GUID, e.g. the five Wasteland barriers NK used, were left untouched). `describe_level_cards` in NK matches AP's cards for all 101 (id, GUID, role, cover, scale, height, lift, footprint, bounds), and every reference resolves. Overrides: `Nuclear Knights/AssetCatalog/LevelCatalogOverrides.json` (canonical, NK `.gitignore` keeps only it under `AssetCatalog/`), identical copy in AP. Deferred: buildings and container sizes may get more tweaks, wall runs with end caps, dressed buildings, ground lift and collider fitting (4c).
- Commits: nothing in NK until the owner approves; then one NK commit with the imported pieces, the overrides file and the `level-rules.md` ranges. USB gets only plan/doc updates (and any gallery helper code). AP has nothing to commit.
- Exit: every approved piece is in NK with an approved role and `placementScale` in the committed overrides file, NK generates matching cards for them, and `level-rules.md` has the confirmed height ranges.

*4c: Wire-up (USB + NK).*
- [x] Apply and bake read `placementScale`, `lift` (pivot height above the floor, added in 4b for pieces modelled below their pivot) and footprints from the cards instead of using the prefab's own scale (and the measured-footprint stopgap from Phase 2 goes away).
- [x] **Card cover reaches the piece hook** (2026-10-17). Apply passes the card's `cover` to `ConfigurePiece`, including `None` for movement-only blockers (role `blocker`, cover `None`, e.g. the Wasteland tyre barriers): NK then adds a `GridObstacle` with cover None, which blocks movement but not sight (NK `GridVisibility` and `NkGridRules.IgnoredBySight` now ignore Half and None obstacles; changed in 4b). Bake and the validators must keep None distinct from Half.
- [x] **Fit colliders to overridden footprints** (agreed 2026-10-17). A card's `footprintOverride` (`[x, z]` meters, added in 4b) lets a piece claim fewer cells than its bounds and overhang them visually, e.g. Tyre_Pile_02 at (0.9, 1, 0.9) claims 1 × 1 while measuring 2.21 × 2.31 m. NK occupancy is any collider sliver in a tile, so apply must make the blocking collider match the claimed cells: add a box collider (with `GridObstacle`, cover and kinematic Rigidbody via the piece hook) fitted to the footprint inset 0.1 m, and disable the piece's own colliders that reach outside it, mesh colliders included (today non-box colliders are only reported). Bake reads such a piece back at its card footprint, and the occupancy check must agree.
- [x] **Ground lift against z-fighting and clipping** (agreed 2026-10-17). The road kit's tiles are a plane at the pivot (card height 0.00 m) and the sidewalk tiles' kerb skirts hang down to −0.25 m; apply puts the pivot on the floor, so roads z-fight the terrain and sidewalks clip into it. Apply lifts ground-role pieces by one game-supplied offset (an `IGridRulesAdapter` value; NK `NkGridRules`), the **same for the whole kit** so road and sidewalk tiles stay level with each other: about 0.25 m clears every skirt (owner check in the 4b gallery, after 0.02 m still clipped). If ground pieces ever overlap (dirt roads or decals on the road kit), give each layer a few cm more. Not Y-scaling (useless for a top at the pivot, and it changes kerb heights), depth bias on the shared Synty materials, or camera clip planes. Bake must accept the offset; a road tile's free edge shows the 0.25 m step, so ground runs should reach the board edge or meet other ground pieces. The gallery's `GroundLift` constant (AP `LevelGalleryGenerator.cs`) uses the same value. Not added to `NkGridRules` in 4b because no layout layer or apply step places ground pieces yet.
- The export of the chosen pieces to NK *moved to 4b* (only approved pieces are imported).
- Exit: LayoutTest still round-trips, Validate Layout Reports and Round Trip pass, and a layout using a few exported pieces applies and validates cleanly. **Done 2026-10-17** (nothing committed yet; owner to approve). How it works: [grid-layouts.md](grid-layouts.md#where-pieces-cards-come-from).
- 4c results:
  - **Where cards come from in a game project:** built on demand (`USB:Editor/Layout/LevelCardSource.cs`, `ForProject()`) from the prefab and `<Project>/AssetCatalog/LevelCatalogOverrides.json` with the generator's `BuildCard`, once per prefab per apply or bake; NK commits no generated catalog, so nothing goes stale. Only **reviewed pieces** (a per-piece entry by GUID or id; `LevelCatalogOverrides.HasPieceEntry`) have a card, so NK's Wall SF, Wall SF (half) and Grid Crystal Patch keep the Phase 2 instance measurement (`PieceGeometry` stays as that fallback rather than going away). A malformed overrides file stops apply/bake before the scene is touched. `Apply`/`Bake` take an optional `LevelCardSource` (tests pass their own).
  - **Apply with a card:** root scale = `placementScale`; footprint = `LevelCards.Footprint(card, cellSize, blocking)` (new `blocking` argument: collider bounds for a layout blocker, visual bounds for decor, whatever the card's role); pivot at floor + `lift` (+ `IGridRulesAdapter.GroundLift` for role `ground`; new interface member, NK 0.25 m); cover = the card's `cover` (adapter `DescribeCover` only when null) passed to `ConfigurePiece`. `LayoutApplyResult.CardCount` counts card pieces.
  - **Collider fitting, broader than planned:** every Synty piece has a single mesh collider, and pieces scaled to whole cells (2.02 m jersey barrier, 3.97 m sandbag run) would reach into the next cell, so the fitting applies to **all** card blockers, not only overridden footprints. Before the hook, each solid collider reaching past the footprint inset (0.1 m) is shrunk if it is an axis-aligned box, else disabled; disabled ones are replaced by one box on a `Footprint Collider` child, their bounds clipped to the footprint inset, full height (`FIT_BOX` note). With a footprint override this is the box fitted to the claimed cells (Tyre_Pile_02: 2.21 × 2.31 m → 1.80 × 1.80 m on its one cell). Pieces without a card keep reporting `FIT_UNSUPPORTED`.
  - **Bake:** card pieces are read at their card footprint and scale; `OFFGRID_SCALE` names the card's `placementScale`; height is not checked (lift and ground lift bake back). Cover comes from the game (`DescribeCover`), and `None` was already distinct from `Half` in `LayoutGrid` (`o` on the map, `COVER` diffs); a USB test now pins that.
  - **NK:** `NkGridRules.GroundLift` = 0.25; `ConfigurePiece` unchanged in code (it configures the fitted box like any solid collider and takes the card's cover). New fixture `docs/fixtures/layouts/catalog-pieces.layout.json` (SM_Prop_Barrier_Concrete_01 jersey, Sandbag_Wall_01, Tyre_Barrier_01 movement-only at rot 1, Barricade_01 on its 0.75 m lift, Tyre_Pile_02 on its 1 × 1 override, a Wall SF without a card, two road tiles and a sidewalk tile as decor; 9 blocked cells). **Validate Layout Round Trip** 2,074 checks (was 2,049; the new block checks scales, lifts, ground lift, the fitted boxes, cover None on the tyre barrier and in the game's occupancy, an identical bake, and a rescaled jersey reported against its card); **Validate Layout Reports** 39 (was 37; the fixture gives a clean report, sight tier included). LayoutTest still round-trips and the GridBattle report still equals its fixture.
  - **Owner check in NK** (2026-10-17): the fixture applied into a new scene, `Assets/Scenes/CatalogTest.unity` (own `Catalog Test.asset` and `CatalogTest.layout.json`; menu **Tools → Grid Combat → Apply CatalogTest Layout**). The owner played it: the tyre barrier blocks movement without giving cover (as intended for a movement-only blocker), the other pieces give cover, and the lifted road tiles neither clip nor z-fight with the ground.
  - **LayoutTest removed** (owner, 2026-10-17): CatalogTest replaces it as the scene to look at layouts. `LayoutTest.unity`, `Layout Test.asset` and the Apply LayoutTest Layout menu are gone; `LayoutTest.layout.json` moved to NK `docs/fixtures/layouts/nk-pieces.layout.json`, still the round-trip fixture for pieces without a card (harnesses unchanged: 2,074 / 39 / 21). The menu file is now `CatalogTestMenu` (Apply CatalogTest Layout only). Earlier mentions of LayoutTest in this plan are history.
  - **USB tests:** new `LayoutApplierCardTests` (5, temporary scene + test adapter) and 2 `LevelCardTests` cases; full suite 230 passed, 7 skipped, 0 failed (was 223/7).
  - Follow-ups: the AP gallery's `GroundLift` constant duplicates NK's value (could read the adapter if AP ever gets one); a per-piece scale field in the layout JSON (§3) is still not in the schema; the 4d scale validator can reuse `LevelCardSource`.

*4d: Deferred Phase 3 validators (after 4c).*
- [x] The plausibility and scale validators from Phase 3, now that cards supply roles, `placementScale` and height ranges. Note: USB's role detection still uses `LevelCards.FullCoverMinHeight = 2.0` (so a cover-named piece at 1.6–2 m is detected as `blocker`); the game's bands (NK: full from 1.6 m) should come from the adapter, not that constant. Heights are bounding-box tops, so thin parts overstate them (Fence_04: end posts make it 1.23 m while its panel is lower); pieces whose role and scale come from an owner-approved override are the reviewed truth and should get at most a note, not an error, when outside their band.
- Exit: scale and plausibility reports are cell-addressed lines with heights and suggested scales, from the game's bands; the clean fixtures stay clean and a broken fixture reports exactly its lines. **Done 2026-10-17** (nothing committed yet; owner to approve). How it works: [grid-layouts.md](grid-layouts.md#validators).
- 4d results:
  - **Bands from the game:** `GridRules.CoverHeights` (`CoverHeightBands` `HalfMin`/`HalfMax`/`FullMin`, USB defaults 0/1.2/2.0) instead of a new interface member, so test adapters need no change; NK `NkGridRules` sets 0.9/1.2/1.6 (`DescribeCover` reads `HalfMax` too). **Role detection** uses them: `LevelCatalogGenerator.BuildCard` takes the project adapter's bands (`LayoutAdapters.CoverHeights()`, USB defaults without an adapter, so AP's catalog is unchanged). In NK all 101 approved pieces have role overrides, so no NK card changed.
  - **Scale** (`LayoutValidator.Scale`, pure, on `PlacedCard`s that apply and bake now record): `HEIGHT_BAND` checks the card height (lift included) against the band of the cover the piece gives as placed (cover None any height; decor unchecked), with a suggested Y scale for the part above the lift (three decimals, rounded towards the band) and the other cover when it fits; a **note** when role and scale are both overrides (new `IssueSeverity.Note`, a "Notes" section; the header counts notes only when there are some, so existing reports print unchanged). `OFFGRID_SCALE` for a card piece in a scene is now cell-addressed with its height at both scales; the bake keeps its `~x,y` line in `Issues` (`OffScaleIssues`) and `ValidateScene` swaps it. Of NK's 101 cards only Fence_04 (1.23 m half) is out of band: a note.
  - **Plausibility** (`LayoutValidator.Plausibility`, warnings): `ROLE_LAYER`, `GROUND_RUN` (4-connected run of ground cells reaching no board edge), `GROUND_OVERLAP`, `BUILDING_FRONT` (front faces the edge or only blocked cells), `BUILDING_NO_FRONT` (note). Cards got `front`/`frontSource` (§3 *Sockets*): the model's forward, +Z turned by the root rotation (`"forward"`), unless a new overrides field `front` (`+x`/`-x`/`+y`/`-y`, grid axes at rotation 0) says otherwise. A door-children heuristic was tried first and dropped: only 3 of the 19 approved buildings have door children, and the owner's gallery check (2026-10-18) showed all buildings face +Z while the doors gave −y for Shop_Medium_02 (its back door) and a front/back tie for Warehouse_Brick_01. So every NK building has a front without overrides. Wall-run `ends` sockets are still not built (Phase 5 end caps).
  - **NK occupancy fix found on the way:** the first applied buildings reported `MULTI` on every cell their door or barricade children cover (the piece hook gives each solid child collider its own `GridObstacle`). `OccupancyBaker` now reports `MULTI` only for obstacles of different pieces (Rigidbodies) or covers; GridBattle and the structural fixture's real overlap still report.
  - **Tests:** USB `LayoutScalePlausibilityTests` (20); `scripts/test-unity.sh` 250 passed, 7 skipped, 0 failed (was 230/7). NK **Validate Layout Reports** 56 (was 39): new fixture `docs/fixtures/layouts/scale-plausibility.layout.json` (20 × 20: Fence_04 note, Bld_Building_08 facing the top edge, Shop_Medium_02 facing four tyre barriers, mid-board road run with a sidewalk tile on a road tile) reports exactly its five lines; a Wasteland barrier with its `placementScale` override removed is a `HEIGHT_BAND` error (1.48 m, Y 0.808); a rescaled jersey in a scene gives the cell-addressed `OFFGRID_SCALE` plus its `EXTRA_BLOCK`; the catalog-pieces and nk-pieces fixtures stay clean and the GridBattle report fixture is unchanged. **Validate Layout Round Trip** 2,074 and **Validate Layout Baker** 21 unchanged.
  - **Shortlist trimmed** (owner, 2026-10-18): 22 pieces removed from NK (override entries, prefabs and the 43 models, materials and textures nothing else used; 130 files with metas): 17 buildings, keeping Bld_Building_08 and Bld_Shop_Medium_02 as the candidates for enterable buildings, and the plain walls #72–#75 and #77 (no structures from modular walls planned). 79 pieces remain; AP's overrides copy and `level-shortlist.json` follow. Harnesses unchanged (56 / 2,074 / 21). Next: walls on cell sides, so those two buildings can be entered on the ground floor (planned as Phase W, below).
  - Follow-ups: ground runs don't know straights from corners or ends, so a run that reaches the edge passes even if its tiles don't line up (that needs per-tile sockets); `validate_layout` as an MCP tool is Phase 5.

**Phase W: Walls on cell sides (enterable ground floors).** Planned 2026-10-18. The owner answered the open questions the same day (*Owner decisions* below) and approved W1 and W2, which were implemented the same day (results under *Sub-phases*; nothing committed). Goal: units can walk into SM_Bld_Building_08 and SM_Bld_Shop_Medium_02 on the ground floor, take cover behind their walls from inside and outside, and be seen and shot only through openings. Upper floors and roofs stay unreachable (D5; a second grid level comes later). Decision D12 (§6) records the owner's choices this phase builds on.

*Today.* A building is one solid blocker: the piece hook gives each solid collider a `GridObstacle`, so every footprint cell is blocked, and the neighbours get cover from it. NK cover is per tile side but comes only from obstacles in neighbouring cells; movement is blocked only by occupied cells.

*Agreed mechanics (owner, 2026-10-18).*
- **Stay cell-based.** Each cell stores, for each of its 4 sides, the cover a wall on that side gives (`None`/`Half`/`Full`) and whether movement may cross it. There is no edge geometry beyond that.
- **One wall, two cells.** A wall sits between two cells and is stored on both, so it gives cover on both sides (as in XCOM).
- **Straight steps.** A straight step across a walled side is refused.
- **Diagonal steps.** A diagonal step is refused if any of the four sides meeting at the corner it passes is walled. This extends `GridMap.CutsObstacleCorner`; the obstacle rule stays.
- **One step rule.** A single `GridMap.CanStep(from, to)` covers the wall and corner rules, and both `FindPath` and `GetReachableNodeSteps` use it. It does not include target occupancy, because `FindPath` closes occupied nodes but must not close a node that is only refused by a wall or corner (the same reason `CutsObstacleCorner` uses `continue` today). Costs, AP and heuristics are unchanged.
- **Cover.** `GridTile.GetTileCoverInfo` returns, per side, max(cover of the obstacle on the neighbour, cover of this cell's wall on that side). `RangedAccuracy.CoverAgainst`, `GridMovement` auto-crouch and the cover icons follow without changes.
- **Sight stays physics.** The building's mesh collider stays on its sight layer (Default) without a `GridObstacle`, so it blocks sight and line of fire except through openings.
- **Footprint.** A building's footprint cells become walkable.
- **Where wall data lives.** A new NK component on the building, `GridWalls`, holds the walled and door sides per footprint cell in the building's own frame, turned by its yaw. `GridManager` registers it at start. USB apply writes it from the building card's outline, and bake reads it back.

*Owner decisions (2026-10-18, after the first draft).*
1. **Building_08:** scaled (1.15, 1, 1.15) in the AP gallery; at that scale the walls line up with the grid lines and enough of the doorway is in its cell. A unit clipping the door frame a little is acceptable.
2. **Shop_Medium_02:** scaled (1.1, 1, 1.1) in the AP gallery; the walls then sit on the grid lines.
   - The building bleeds into the cells to its left and right, and its front overgrowth into the cells in front; those cells stay passable.
   - **Keep the boards:** the front stays barricaded, so the shop is entered through the back door only.
   - The owner disabled `SM_Bld_Shop_Medium_02_Door_01` (the closed back door and its `BoxCollider`) in the prefab.
3. **Windows give Half cover.**
4. **Cutaway when the cursor hovers** over the building's cells. A second grid level is planned later.
5. **Door points: yes.** Units path through the door's world position even where the doorway doesn't line up with the cell, stored as a child transform on the building (see *Door points*).
6. **Path distance for the AI approach,** in W1: `GetClosestWalkableGrid` ranks by path steps instead of straight-line distance, so enemies go round to a door instead of stopping at the outside wall.

The owner's scales and the disabled door are on Asset Preview's prefab roots (`Assets/Synty/...`, saved 2026-10-18 10:58 and 11:09). Both overrides files (NK canonical, AP copy) still say `placementScale` 1, and NK's copies of the two prefabs are unchanged. They are synced at the start of W3 together with the walls entries, because the new scales change both footprints and the 4d `scale-plausibility` fixture (Building_08's bounds are 6.18 × 6.33 m at 1.15, which would round up to 4 × 4 without a footprint override).

*What the two buildings are.*
- **How they were measured.**
  - **Pack scale (2026-10-18, in NK).** On isolated prefab contents: 0.2 m overlap rasters at 0.3, 1.0, 1.7 and 2.6 m; slab heights per cell; top-down cutaways and facade renders.
  - **The owner's scales (the same day, in AP).** Wall faces from rays at 1.0 m across the middle 60 % of each side, and openings at 0.3, 1.0 and 1.7 m along each wall.
  - **Probes.** Temporary Editor scripts, since removed. The captures and numbers are in the session notes folder, `walls-probe/`.
  - **A correction.** The first draft said Building_08's collision mesh had no back wall. That was a raster artefact: the 0.2 m overlap boxes can miss a thin face between samples. Rays hit the back wall, so no sight fill is needed.
- **Coordinates.** Metres from the centre of the interior frame (the cells whose boundaries the walls follow); +y = model +Z = front.
- **SM_Bld_Building_08** (Wasteland; single mesh with a `MeshCollider` from `SM_Bld_Building_08_Collision`, no children).
  - **Frame.** At (1.15, 1, 1.15) the bounds are 6.18 × 6.21 × 6.33 m. The interior frame is **3 × 3 cells**, centred on the bounds (offset under 1 cm).
  - **Walls.** About 0.2 m thick, all four between 0.05 and 0.25 m inside the frame lines (centrelines 0.12–0.15 m inside).
  - **Doorway.** 1.1 m wide on the +y side of cell (2,2), x 1.01–2.11. The cell's centre line (x 2.0) passes 0.11 m inside the opening, so the door point is at −0.44 m along x from the side's midpoint.
  - **Windows.** Open above a sill between 1.0 and 1.7 m: +x of (2,1) (z −0.87 to 0.78) and −x of (0,1) (z −1.32 to 0.38, which reaches 0.32 m into (0,0)'s side).
  - **Storeys** (Y is unscaled). Floor top at 0.09 m, ceiling (upper slab underside) at 2.60 m, upper floor 3.09–5.60 m, roof 5.60–6.00 m. The upper slab has a stair hole over the −x column but no stairs, and there are no interior props.
- **SM_Bld_Shop_Medium_02** (Apocalypse).
  - **Children.** `Barricades_01` (one mesh and mesh collider: boards on the storefront and windows, plus the rooftop junk), `Door_01` (now inactive), and `Ladder_01` and `Overgrowth_01` (decor, no colliders).
  - **Frame.** At (1.1, 1, 1.1) the bounds are 17.64 × 8.11 × 11.32 m. The interior frame is **8 × 4 cells**; its centre is 0.58 m behind the bounds centre, because the front porch and overgrowth reach further out than the back slab.
  - **Walls.** About 0.4–0.5 m thick. The side walls are 0.25 m inside the frame lines and the front and back walls 0.21 m outside them. The brick pillars (about 1 m) reach about 0.8 m into the side cells. The porch, its roof (2.67–3.05 m) and the overgrowth reach about 2.2 m in front (one row of cells plus 0.24 m); the back slab reaches about 1.1 m behind.
  - **Back doorway.** 1.1 m wide on the −y side of cell (1,0), x −6.08 to −4.98. The cell's centre line (x −5.0) is only 0.02 m inside the opening, so this door needs its door point, at −0.53 m along x from the side's midpoint.
  - **Front.** The storefront doorway (2 m, centred) is boarded, so it is wall. The glazed windows are closed in the collision mesh and the front ones are also boarded, so they block sight.
  - **Storeys.** One tall storey, not the card's 3: floor 0.15 m, ceiling 5.17 m, roof slab 5.31 m, rooftop clutter up to about 6.8 m. A patch of floorboards near the centre is missing, so the ground shows through.
- **Floor height vs NK's Y = 0.** Units stand at Y = 0, so they sink 0.09 m into Building_08's floor and 0.15 m into the shop's.
  - Card `lift` −0.09 and −0.15 put the floors at 0; the shop's porch and back slab then sit flush with the ground.
  - `lift` is currently "pivot height above the floor" and must accept negative values.
- **Clearance.** The tile's `VisionCheckPosition` (2 m) is below both ceilings.

*Windows.* A window side (`w`) blocks movement, gives Half cover (decision 3) and is seen through, because sight stays physics. Building_08's two windows are of that kind. The shop's windows are closed in the collision mesh (glazed or boarded), so they block sight like a wall; they are drawn as wall (`Full`) unless the owner wants them `w`. A Half-cover side that blocks sight would behave unlike any Half cover in NK, where Half never blocks sight.

*Outline format (card override, USB).* There is one new `walls` field per building entry in `LevelCatalogOverrides.json` (as built in W2: an object whose `outline` holds the picture, with the optional fields beside it; [level catalog](level-catalog.md)). The picture is ASCII at layout rotation 0 (top row = +y), matching the validators' ASCII maps. For W × H cells it has 2H + 1 lines of 2W + 1 characters:
- **Even lines** are the horizontal sides: `+` at even columns, a side character at odd ones.
- **Odd lines** are the cell rows: a side character at even columns, the cell at odd ones (a space).
- **Side characters.**
  - `-` or `|`: a wall (cover `wallCover`, default `Full`).
  - `w`: a window (no movement; cover `windowCover`, default `Half`).
  - `D`: a door (movement allowed, cover `None`).
  - space: an open side.
- **Interior sides** use the same characters, so partitions are possible.
- **Optional fields** (inside `walls`, with `wallCover` and `windowCover`).
  - `doorPoints`: `[[x, y, side, offsetMetres]]`, the doorway's centre along the side, from the side's midpoint, at most ±0.9 m.
  - `sightFill`: `[[x, y, side]]`, wall sides whose collision mesh has a gap. Apply adds a box there; neither building needs one.
  - `cutHeight` (cutaway, metres above the floor).
  - `roofed`: rectangles `[[x0, y0, x1, y1]]` of extra roofed cells outside the frame, such as the shop's porch row.
- **Related fields.**
  - A new `footprintOffset` (`[x, z]` metres at rot 0, the model's bounds centre relative to the frame centre) joins the existing `footprint`, which is the frame (6 × 6 and 16 × 8 m).
  - Building_08 gets `lift` −0.09; the shop gets `lift` −0.15 and `storeys` 1.
  - Both get the owner's `placementScale`.

Building_08, at `placementScale` (1.15, 1, 1.15), as the W2 suggester proposed it from AP's prefab:
```
"footprint": [6, 6], "footprintOffset": [0, 0], "lift": -0.09,
"walls": {"outline": ["+-+-+D+",
                      "|     |",
                      "+ + + +",
                      "w     w",
                      "+ + + +",
                      "|     |",
                      "+-+-+-+"],
          "doorPoints": [[2, 2, "+y", -0.45]]}
```
Shop_Medium_02, at (1.1, 1, 1.1): `"footprint": [16, 8], "footprintOffset": [-0.01, 0.58], "lift": -0.15`, and a 17-character picture whose top line is all wall, whose interior lines are open, and whose bottom line is `"+-+D+-+-+-+-+-+-+"`, with door point `[[1, 0, "-y", -0.5]]`. The porch row would be `"roofed": [[0, 4, 7, 4]]` for the cutaway (W5; the suggester does not propose roofs). Pieces rotate the picture, offsets and door points (`WallOutline.Rotate`).

*Door points.* An optional point per door side, where units cross it.
- **Stored as a child transform.** Apply adds an empty child, `Door Point x,y side`, under the building at the doorway's centre on the wall line at floor height (from the card's `doorPoints`), and `GridWalls` references it in that side's entry. Bake reads its position back into the offset. The owner can also move it in the scene, and the next bake reports the difference from the card.
- **Visual only.** `GridMap` and its paths stay in cells: the side is still the cell's side, AP and costs are unchanged, and A* is unchanged.
- **How movement uses it.** `GridMovement` moves from a cell centre to the door point and then on to the next cell centre.
  - Today every entry in `_completePath` is a tile: reaching one charges AP, fires `OnTileReached` and checks contact. So the door point cannot be an extra path entry.
  - Instead, `GridManager` returns the tile path plus a via point per step (none for most steps), and `GridMovement` passes a via point without charging, firing or stopping. It turns towards the next target without the stop-and-rotate it does at tile centres.
- **Stays inside the two cells.** The point lies on the shared side within ±0.9 m of its midpoint, so a unit never leaves the two cells and the tile triggers see the same enter and exit order.
- **Diagonal moves.** Diagonals never cross a door side, because the four-sides corner rule refuses them next to a doorway's walls.

*NK changes* (`NK:Scripts/GridSystem/`).
- **`PathNode`** gets `BlockedSides`, a `GridSideFlags` bitmask (+x, −x, +y, −y) that only walls set; units never touch it.
- **`GridMap`.**
  - `CanStep(from, to)`: the target is adjacent; a straight step's side is not blocked; a diagonal is refused when either orthogonal neighbour `HasObstacle` (today's rule) or any of the four corner sides is blocked (`from`'s x and y sides, the y side of (to.X, from.Y) and the x side of (from.X, to.Y)). Walls are stored on both cells, so these four cover the corner.
  - `CutsObstacleCorner` stays as a wrapper so existing callers and harnesses keep compiling.
  - `FindPath` and `GetReachableNodeSteps` call `CanStep` after the occupancy check, with `continue`, not close.
  - `ShowSelectableGrids` (attack-range highlight, geometric) is left alone.
- **`GridTile`.** Gets `SetWallCover(AdjacentTile, CoverType)`, and `GetTileCoverInfo` takes the max with the neighbour's obstacle cover. The cover icons use the same dictionary.
- **`GridWalls`** (new MonoBehaviour on the building root).
  - It holds the outline in the piece's rot-0 grid frame as serialized per-side entries (cell, side, kind, cover, door point `Transform`), plus roofed cells, cut height and the building's renderers.
  - At registration it maps the frame's cell centres through the transform (yaw snapped to quarter turns; another yaw or a frame off the board is an error) and sides through the yaw.
  - It also takes over `GridObstacle`'s fog role (`Reveal`, adding the stencil mask material when one of its cells or their neighbours becomes visible), since the building no longer has a `GridObstacle`.
- **`GridManager`.**
  - Registers every `GridWalls` in the scene in `Awake` right after `InitGridTiles`, not in each component's `Start`: `CharacterLoader.Start` begins the battle and its order against other `Start`s is undefined. Registering sets `BlockedSides` on both cells of each walled side and tile wall cover on both.
  - `GetWorldSpacePath` also returns the via points.
  - `GetClosestWalkableGrid`/`FindClosestNode` rank reachable cells by path steps to the target, not straight-line distance (decision 6). Ties go to the shorter move, and the target cell itself is never entered.
- **`GridMovement`.** Via points (above).
- **`NkGridRules`.** Restates the rules: `GridRules.SideWalls = true`; `ConfigurePiece` adds no `GridObstacle` to any collider of a walled piece (today the shop's barricade child gets one, which would block the cells again through the triggers); new `ConfigureWalls`/`ReadWalls` hooks write and read `GridWalls` and its door point children; `IgnoredBySight` is unchanged.
- **`OccupancyBaker`.** Also reports side walls from `GridWalls`, so the game's occupancy bake can be compared with the layout's walls.
- **Behaviour that follows automatically.** Line of sight, line of fire, bug and melee range (`HasLineOfFireAtPosition`) and the waist cover ray stay physics. The building collider has no `GridObstacle`, so the waist ray credits nothing from it, and wall cover comes from the tile rule.
- **AI.** There is no cover-seeking AI today: `EnemyFiringPositionPlanner` ranks reachable cells by hit chance and gets walls through `GetReachableNodeSteps`. The approach change above is the only AI change.
- **Harnesses (D8, menu items).**
  - **Validate Paths:** the independent BFS gets an independently written wall and corner rule; random walls on the random maps; cases for a straight step across a wall, each of the four corner sides refusing a diagonal, a door passage, a sealed room and a unit standing in a doorway.
  - **Validate Layout Round Trip:** the 25 seeded maps get random side walls, so USB's `LayoutGrid.CanStep` must equal `GridMap.CanStep` on every step.
  - **Action Points:** a detour through a door costs the tiles entered, and a via point charges nothing.
  - **Enemy Firing Positions:** a target behind a wall is reached through the door, and the path-distance approach picks the doorway side.
  - **Bug Attack Range:** a bug outside a closed wall cannot attack.
  - **Validate Layout Baker:** a building turned 90° maps its door side and door point correctly.
  - **GridBattle has no `GridWalls`**, so every existing count stays the same. The approach change can move enemies differently around GridBattle's obstacles, so it is spot-checked in Play Mode.
- **Docs.** `level-rules.md` (Movement, Occupancy, Cover and Line of sight, plus a Buildings section) and `combat-flows.md` (movement, enemy approach), in the same changes.

*USB changes.*
- **`LayoutGrid`.**
  - Per-cell side walls (blocked and cover, stored on both cells).
  - `CanStep` adds the wall and four-sides corner rules when `Rules.SideWalls` is set.
  - `CoverFrom`/`CoverSides` become per side: max(neighbour blocker cover, own wall cover).
  - A claim layer, so two buildings can't share cells while other pieces may stand inside one.
  - `DiffOccupancy` adds `WALL_MISSING`/`WALL_EXTRA`/`WALL_COVER`.
  - `ToAscii` prints a double-resolution map (the outline characters) only when the grid has walls, so existing report fixtures print unchanged.
- **`GridRules.SideWalls`**, default false, so other games and test adapters are unaffected.
- **Cards and overrides.** `WallOutline` (pure: parse, validate, rotate, sides per cell, door points), `LevelCard.Walls`/`FootprintOffset`, negative `lift`, and `PieceSpec.Outline`. `LayoutGrid.Build` claims a walled piece's frame without blocking it and adds its walls.
- **Apply.**
  - Walled pieces skip the 4c collider fitting: today every card blocker's mesh collider is disabled and replaced by a `Footprint Collider` box, which would fill the interior.
  - Pieces are placed at `footprintOffset`; apply adds `Door Point` children and a `Wall Sight Collider` box per `sightFill` side.
  - The new adapter hook `ConfigureWalls` writes `GridWalls`.
- **Bake.** Reads `ReadWalls` back into the outline and door points and reports `WALLS_DIFF` against the card.
- **Validators.**
  - **Reachability** runs through doors automatically (`CanStep`), with a new `BUILDING_SEALED` (warning: no door reachable from deploy).
  - **Doors.** `DOOR_BLOCKED` (a door's outside cell is blocked) and `DOOR_OFF_BOARD`; for walled buildings, these door checks replace `BUILDING_FRONT`'s front check, since the shop's front is boarded and its door is at the back.
  - **Structural.** `WALL_CROSSES_PIECE` (a blocker's footprint spans a walled side), `OVERLAP` for building claims, and `DOOR_POINT_RANGE` (a door point more than 0.9 m from its side's midpoint).
  - **Physics tier.** `WALL_SIGHT_GAP`: rays across each `Full` wall side at 1.0 and 1.7 m must hit a sight collider, tested with a synthetic fixture (neither building has a gap). A collider inside a door side at walking height is reported (it would have caught the shop's closed `Door_01`), and colliders of walled pieces no longer count as `SIGHT_NO_OBSTACLE`.
  - **Fixture.** The 4d `scale-plausibility` fixture's two `BUILDING_FRONT` lines change once the buildings get walls; update its expected lines in W4.
- **Outline suggester** (Editor helper, from the probes).
  - On isolated prefab contents at the card's scale, it proposes the frame, `footprintOffset`, the outline (walls, doors, windows from openings at 0.3, 1.0 and 1.7 m), door points, `sightFill`, floor `lift` and storeys.
  - It uses rays, not overlap rasters, which missed thin faces. It prints a top-down cutaway with the grid, rendered from a preview scene with an instance (rendering the prefab-contents scene came out empty).
  - The agent runs it and the owner approves the result into the overrides file (as with `placementScale` in 4b).
- **Tests.** NUnit for `WallOutline`, `LayoutGrid` walls (steps, corners, cover, diff, ASCII), build and validators; `LayoutApplierCardTests` gains a walled test piece.
- **Docs.** `grid-layouts.md`, `level-catalog.md`.

*NK rendering.*
- **Cutaway.** From the top-down camera the roof hides the interior. Both buildings are one mesh each (floors, walls and roof together), so renderers cannot be hidden piece by piece.
  - Plan: a cutaway variant of the Synty material (URP Shader Graph with world-space `_CutHeight` and a bounds box), set per renderer by `GridWalls`.
  - When to cut: **when the cursor hovers** over one of the building's frame or roofed cells (decision 4).
  - Cut height about 2.4 m: below Building_08's ceiling and the shop's porch roof.
  - Starts with a spike, because cut walls show their back faces unless the shader caps or renders two-sided.
- **Fog.** `GridWalls.Reveal` keeps today's stencil-material reveal for buildings, checked with both per-tile and continuous fog.
- **To check.** Clicks use the "WorldSpace click plane" layer, so roofs don't intercept tile clicks; `AimView`'s pointer raycast over a unit inside a roofed building still needs checking.

*Sub-phases (each with its exit).*
- [x] **W1 Game rules (NK only).** Done 2026-10-18.
  - Work: `PathNode.BlockedSides`, `GridMap.CanStep`, tile wall cover, `GridWalls` (data, door point transforms, registration; no rendering), `GridManager` registration and via points, `GridMovement` via points, the path-distance approach, harness updates, `level-rules.md` and `combat-flows.md`.
  - Exit: Paths, Action Points, Enemy Firing Positions and Bug Attack Range pass with the new cases and unchanged counts for the old ones. In a scratch scene, a hand-configured `GridWalls` on Building_08 at (1.15, 1, 1.15) lets a unit walk in through the door, via its door point, in Play Mode. The cover icons and auto-crouch match the outline (Half at the windows), and a diagonal past the door frame is refused. GridBattle plays as before.
  - W1 results (2026-10-18, nothing committed):
    - **Code.** NK `TileSides.cs` (`TileSides` flags, `TileSide` helpers), `GridWalls.cs` (frame size, frame centre in root space, authored yaw, `WallSide` entries with an optional door point `Transform`; placement in quarter turns, off-grid or off-yaw reported), `PathNode.BlockedSides`.
    - **`GridMap`.** `CanStep` (wall and four-sides corner rule; `CutsObstacleCorner` is now `!CanStep` for old callers), `BlockSide`, `SetDoorPoint`/`GetViaPoints`, `GetStepDistancesFrom` and `FindClosestReachableNode` (path-distance approach, straight-line fallback when the target connects to nothing affordable).
    - **Tiles and manager.** `GridTile.SetWallCover` (cover max with the neighbour's obstacle). `GridManager.RegisterWalls` runs in `Awake` after the tiles; `GetWorldSpacePath(..., out viaPoints)`; `GetClosestWalkableGrid` uses path steps.
    - **Movement.** `GridMovement` walks via door points without charging AP or firing `OnTileReached`, turning while walking past them.
    - **Harnesses** (the old fixtures keep their seeds): Validate Paths 2,956 (was 1,464); Action Points 67 (66); Enemy Firing Positions 23 (21); Bug Attack Range 22 (20). Validate Layout Reports 56 and Baker 21 unchanged; Round Trip 2,075 after W2. NK `level-rules.md` (Movement, Cover, new *Buildings: walls on cell sides*) and `combat-flows.md` (movement, approach row) updated.
    - **Play Mode check** (2026-10-18, after the owner saved CatalogTest), in a scratch copy of CatalogTest (its generated layout removed) with Building_08 at (1.15, 1, 1.15) on cells 3–5 × 1–3, a hand-made `GridWalls` (walls Full, windows Half on −x of (3,2) and +x of (5,2), door on +y of (5,3) with a door point 0.44 m off-centre) and a temporary probe:
      - The frame cells stayed walkable, and the walls were stored on both cells.
      - Both diagonals past the door frame were refused and the straight step through the door allowed.
      - Cover read Half on both sides of each window, Full outside the front wall and None in the doorway.
      - Reno walked from (1,1) round the house and in through the door to (4,2) (`1,1 2,2 2,3 2,4 3,4 4,4 5,4 5,3 4,2`), passing 0.07 m from the door point, with 8 tiles reached for 8 path steps (the via point counted no tile).
      - Not shown: auto-crouch, because Reno can't crouch; it reads the same cover dictionary.
      - Inside, Reno is hidden by the roof, which is the W5 cutaway's job.
      - The scratch scene and the temporary scripts were deleted afterwards, and CatalogTest reopened unchanged. The probe output and screenshot are in the session notes folder (`walls-probe/walls-w1-playmode.txt`, `w1-playmode-reno-inside.jpg`).
    - **Owner test setup** (2026-10-18, on request): a hand-configured walled Building_08 was added to CatalogTest and the scene saved.
      - It is a scene root outside `[Generated Layout]`, on cells 0–2 × 8–10, turned 180° so its door (−y of (0,8)) faces the deploy cells.
      - Play Mode smoke test: registered without errors.
      - Described in NK `level-rules.md` § *CatalogTest scene conventions*. W3 replaces it with a layout-applied building.
- [x] **W2 Layout model and outlines (USB, can run alongside W1).** Done 2026-10-18.
  - Work: `LayoutGrid` walls, `GridRules.SideWalls`, `WallOutline` and the overrides fields, negative lift, NUnit tests, outline suggester.
  - Exit: tests pass, and the suggester's entries for both buildings at the owner's scales are written up for the owner with cutaways.
  - W2 results (2026-10-18, nothing committed):
    - **Model.** USB `Runtime/Layout/WallOutline.cs` (`SideKind`, `OutlineSide`, `DoorPoint`; the ASCII picture both ways, JSON, rotation with door points and roofs, placement). `GridRules.SideWalls`, `PieceSpec.Outline`.
    - **`LayoutGrid`.** Sides (`SetSide`/`SideAt`/`IsWalled`), claims, the wall and corner rule in `CanStep`, `CoverOn`, the `WALL_*` diff lines, the double-resolution map, `Build` claiming walled frames (`OVERLAP` between buildings, `OUTLINE_SIZE`).
    - **Cards and overrides.** The overrides fields `walls` and `footprintOffset`, and the card's `Walls` (buildings only) and `FootprintOffset`, written to card JSON only when set. Negative `lift` was already accepted.
    - **NK.** `NkGridRules.Rules.SideWalls = true`. Round Trip's 25 seeded maps carry random walls and doors from their own seed (2,075 checks, was 2,074).
    - **Outline suggester.** `Editor/Layout/WallOutlineSuggester.cs` (rays, not overlap rasters).
      - It finds the wall centrelines and fits the frame, then classifies each side by sampling it at 0.3, 1.0 and 1.7 m: a door is an opening at least 0.6 m wide; a window is solid at 0.3 m and open at 1.7 m; a wall is mostly solid at 1 m.
      - It also proposes door points, sight gaps, the floor lift and the ceiling. Only the middle of a side counts for interior solids and sight gaps, so door frames and corners don't leak.
      - `RenderTopDown` writes a cutaway with the frame.
    - **On AP's prefabs** (the owner's scales, the shop's `Door_01` off) it proposed exactly the plan's entries (above): walls within 0.12–0.25 m of the lines, Building_08's door point −0.45 m, the shop's −0.5 m, lifts −0.09 and −0.15, ceilings 2.60 and 5.17 m, no sight gaps. The output and cutaways are in the session notes folder (`walls-probe/wall-suggestions.txt`, `*-suggested-cut1.9.png`).
    - **Tests.** USB `WallOutlineTests` (16) and `WallOutlineSuggesterTests` (2); `scripts/test-unity.sh` 268 passed, 7 skipped, 0 failed (was 250).
    - **Docs.** `grid-layouts.md` and `level-catalog.md`.
- [x] **W3 Apply, bake and the owner check (USB + NK; needs W1 and W2).** Done 2026-10-18 except the owner check in CatalogTest (see the results).
  - Work:
    - Sync the owner's AP prefab changes (root scales, `Door_01` inactive) into NK's two prefabs, and the approved entries into both overrides files.
    - Adapter hooks; skip fitting and `GridObstacle` for walled pieces; door point children; NK `OccupancyBaker` walls.
    - Fixture `buildings.layout.json`: both buildings, one turned, a deploy outside and a raider inside the shop. Round Trip with random walls.
  - **Owner check** in CatalogTest (or a new BuildingTest scene): offsets, lifts, window cover, the door points.
  - Exit: apply then bake is identical including walls and door points, the game's occupancy and walls equal the layout, and Validate Layout Round Trip and Reports pass. In Play Mode a squad walks in through each door and takes wall cover inside and outside.
  - W3 results (2026-10-18):
    - **USB `IGridRulesAdapter`.** Two hooks with default implementations (no-op and false), so other games and test adapters are unchanged:
      - `ConfigureWalls(instance, outline, frameCentre, layoutRotation)` writes the game's walls.
      - `TryReadWalls(instance, out outline)` reads them back with door points.
    - **USB apply.** Every card piece is placed at its `footprintOffset`. A walled building (a card with `walls`, `GridRules.SideWalls`) skips `ContainSolidColliders`, `ConfigurePiece` and `FitColliders`, so its mesh collider stays for sight and no `GridObstacle` is added. It gets `ConfigureWalls` instead and a `PieceSpec` with its outline.
    - **USB bake.** It reads card pieces back with the offset, rebuilds walled specs, and reports `WALLS_MISSING` or `WALLS_DIFF` (outline, covers, door points within 5 cm) against the card.
    - **NK `NkGridRules`.**
      - `Classify` treats a `GridWalls` root as a piece.
      - `ConfigureWalls` adds `GridWalls`, with `Door Point x,y side` children placed from the outline, and sections the building for the cutaway (W5).
      - `TryReadWalls` reads the outline and door points back from `GridWalls`.
      - `BakeOccupancy` adds every `GridWalls`'s sides to the game's grid, or `WALLS_UNPLACED`.
    - **Owner's changes synced into NK.** Prefab root scales (Building_08 (1.15, 1, 1.15), shop (1.1, 1, 1.1)) and the shop's `Door_01` inactive were edited in NK's own prefab files; AP's serialize with a newer Unity, so they were not copied.
    - **Overrides entries for both buildings** (NK canonical, AP copy identical): `placementScale`, `footprint` [6, 6] and [16, 8], `footprintOffset`, `lift` −0.09 and −0.15, `storeys` 2 and 1, and `walls` with door points; the shop also has `roofed` (porch row).
    - **Fixture `buildings.layout.json`** (16 × 14): Building_08 turned twice, the shop with a sandbag wall and a raider inside.
      - Validate Layout Round Trip checks each building's frame size and centre, the kept mesh collider with no `GridObstacle` or fitted box, and its walls placing on the board.
      - It also checks door point positions to 1 cm, walls read back as the card's, the cutaway chunks, the frames walkable and claimed, and turned doors and windows on the right sides.
      - Apply then bake is identical and the game's walls equal the layout's; editing a window in the scene gives `WALLS_DIFF` and `WALL_KIND`.
      - Round Trip has 2,100 checks (was 2,075).
    - **CatalogTest** (2026-10-19): both buildings applied from the layout, replacing the hand-configured test building; the apply gave a clean report.
      - After the owner's first look the board grew to 20 × 20, and the house moved to 15,4 so it no longer crowds the shop at 4,11. The owner then turned it in the scene (rotation 3, door on −x of 15,6 facing the deploy cells), and the door kept working; a bake read the turn back with no issues, and the layout JSON was updated to match. The raider starts inside the shop.
      - **Pointer fix** from the same look: the mouse could not target the raider inside the shop, because `AimView` takes the closest hit and that was the building's collider. While a building is cut away, the pointer now skips its colliders (`BuildingCutaway.IsSeeThrough`); sight, shots and cover still use them. Not yet checked with a real mouse.
- [x] **W4 Validators (USB + NK).** Done 2026-10-18.
  - Work: `BUILDING_SEALED`, door checks, `DOOR_POINT_RANGE`, `WALL_CROSSES_PIECE`, claim `OVERLAP`, `WALL_SIGHT_GAP`, the door-collider check, the `SIGHT_NO_OBSTACLE` exemption, double-resolution ASCII; broken fixtures for each, and the 4d fixture's updated lines.
  - Exit: clean fixtures stay clean and each broken fixture reports exactly its lines.
  - W4 results (2026-10-18):
    - **Structural:** `WALL_CROSSES_PIECE` (a solid piece straddling a wall or window) and `DOOR_POINT_RANGE`, in `LayoutValidator.WallStructure`.
    - **Plausibility:** for walled buildings, door checks replace `BUILDING_FRONT`: `DOOR_OFF_BOARD`, `DOOR_BLOCKED` and `BUILDING_SEALED` (no door opens onto an open cell), all warnings. Buildings without walls keep the front check.
    - **Physics tier (`LayoutSceneValidator.WallSight`):**
      - `WALL_SIGHT_GAP`: rays across each Full wall at 1.0 and 1.7 m, three points along it, must hit a sight collider.
      - `DOOR_COLLIDER`: a ray at 1.0 m through a door's point must not.
    - **Already in place:** claim `OVERLAP` and the double-resolution map (W2), and the `SIGHT_NO_OBSTACLE` exemption (walled buildings classify as pieces).
    - **New fixture `walls-broken.layout.json`:** a house whose door opens off the board, a shop whose back door opens onto a jersey barrier, and sandbags straddling the shop's side wall. It reports exactly its eight lines: one `WALL_CROSSES_PIECE`, one `ENEMY_UNREACHABLE`, two `POCKET`s, a `DOOR_OFF_BOARD`, a `DOOR_BLOCKED` and two `BUILDING_SEALED`s.
    - **The 4d `scale-plausibility` fixture** now reports the scrap block's off-board door (`DOOR_OFF_BOARD`, `BUILDING_SEALED`, its `POCKET`) instead of the two `BUILDING_FRONT` lines; its shop's back door is open.
    - **`buildings.layout.json` gives a clean report**, wall-sight tier included, so neither building needs `sightFill` and neither doorway is obstructed.
    - **Counts:** Validate Layout Reports 77 (was 56); the GridBattle report fixture is unchanged. USB tests: `WallOutlineTests` gains structure and door tests, and `LayoutApplierCardTests` a walled building through apply and bake; `scripts/test-unity.sh` 271 passed, 7 skipped, 0 failed (was 268).
- [ ] **W5 Rendering (NK).** Spike started 2026-10-18; the approach changed after discussion with the owner:
  - **Mesh chunks instead of a height-cut shader.** An Editor tool (NK `BuildingSectioner`) splits the render mesh (no Blender) into Upper (above 2.4 m, clipped at the cut), one chunk per outer side of the `GridWalls` frame, and Base.
  - **On hover** (NK `BuildingCutaway`), Upper is hidden and the sides facing the camera are drawn faded: dithered (`NK/Building Dither`, an opaque screen-door fade, chosen over transparency, which already misbehaves with the fog on the crystal patch hologram) or a Fresnel ghost. Far walls stay as they are.
  - **Shadows** stay through shadow-only twins.
  - **The walk camera is off** (`GridCamera` **Use Walk Camera**, code kept), so only the overhead view matters.
  - **Status:** set up on CatalogTest's walls test building (NK 7742e02a).
  - **Owner's choice** (2026-10-18): **dither**, visible fraction 0.3. Fresnel looked good but suits a pure cyberpunk theme better, and it is transparent. Hidden also works but drops the windows and doorways of near walls.
  - **Finished 2026-10-18** except the fog reveal:
    - `BuildingCutaway` is dither only (the Fresnel copy was removed).
    - `BuildingSectioner` moved out of `Editor/` (`#if UNITY_EDITOR`) so the apply hook can call it. It now splits every enabled mesh renderer under the building (the shop's boards, rooftop junk, ladder and overgrowth too) in the root's space, one submesh per distinct material, and rerunning rebuilds the same chunks.
    - `NkGridRules.ConfigureWalls` sections each applied building into `Assets/Generated/Buildings/<prefab>/` (shared by its instances; cut at the outline's `cutHeight` or 2.4 m).
  - **Fog reveal for buildings is not done:** a building has no `GridObstacle`, so `GridObstacle.Reveal`'s stencil material never reaches it. In the owner's Play Mode tests buildings looked right under fog, so whether that matters is still open. The owner then found that windows reveal nothing (the 2 m reveal rays pass above Building_08's openings, and the shop's glazing is solid). Phase S (grid sight) replaces physics sight, which also settles this item.
  - Work: cutaway spike, then the hover-driven `GridWalls` cutaway; fog reveal.
  - Exit: owner Play Mode review (enter, fight from inside, see inside on hover).

*Risks.*
- **Paths change when walls appear.** The harnesses are re-run and enemy behaviour near the buildings is spot-checked.
- **The approach change affects every melee and investigating enemy,** not only near buildings.
- **Single-mesh buildings make the cutaway a shader task.**
- **The collision mesh and render mesh can disagree.** `WALL_SIGHT_GAP` exists for this.
- **The 4c collider fitting** would silently fill interiors if a walled card piece is not exempted. The Round Trip fixture guards it.
- **Via points must not count as tiles.** The Action Points harness guards it.
- **Placement in the plausibility fixture.** Buildings already placed as solid blockers (the 4d fixture) become enterable when their cards get walls.

**Phase S: Grid sight (one sight rule for fog, spotting and fire).** Planned 2026-10-19 with the owner; **plan only, waiting for the owner's approval** (no code yet). Decision D13 (§6).

*Why.* After Phase W the owner found that windows don't work.
- **Fog reveal is tile-to-tile at a fixed height.** `GridManager.GetVisibleNodes` reveals a tile when a physics ray between two tiles' `VisionCheckPosition`s is clear. That point is the Grid Manager's height, 2.0 m in GridBattle and CatalogTest, an arbitrary number.
- **Building_08's openings are lower than 2 m.** Its windows are open only from about 1.0 to 1.7 m and its doorway to 1.9 m, so nothing outside is revealed through them.
- **The shop's windows are solid** in its collision mesh (glazing and boards), so the raider inside stayed fogged until a unit stepped through the door.
- **Unit spotting is a separate test.** `GridVisibility.CanSeeUnit` uses physics from the observer's head point (human 1.55 m, power armor 2.15 m) to the target's body or head. Line of fire uses the same rays. Fixing only the reveal would leave units revealed that can't be targeted or shot.
- **The physics tests are a leftover.** They date from when shots were simulated from each weapon's muzzle; hit chance, cover and eligibility are rule-based now.

*Rule (owner, 2026-10-19).* One grid rule replaces physics sight for fog, spotting and line of fire.
- **The line.** `CanSee(a, b)` walks the supercover line between the two cell centres: every cell and cell side it crosses.
- **What blocks it.** A cell holding a Full-cover obstacle, or a side holding a Full-cover wall (`GridWalls`). Windows (Half), doors, Half-cover and movement-only obstacles, and units don't block.
- **Corners.** A line through an exact cell corner is **blocked** if either way round the corner is blocked. The rule stays symmetric: if A sees B, B sees A.
- **Range.** Vision range stays today's circular range in tiles; night and illumination rules are unchanged.
- **Scenery without a `GridObstacle`** no longer blocks sight. GridBattle's sandy rock sets are play-area borders and backdrop, so seeing through them is fine (owner).
- **No height.** A Full obstacle blocks its whole cell, even a thin wall; walls on cell sides are exact. Height comes back with elevation, as cell levels with floors and roofs blocking between them.

*What switches to it* (NK `Scripts/GridSystem/`):
- **Fog:** `GridManager.GetVisibleNodes`, run from the observer's cell. `GridTile.VisionCheckPosition` is no longer used for sight (illumination positions can stay).
- **Spotting:** `GridVisibility.CanSeeUnit`/`CanFactionSeeUnit` (range, then grid sight between the two units' cells), and through them `TargetDetector`, `AttackThreatMemory`, the contact check in `GridGameManager` and the eligibility check in `GridWeaponBase`'s preview.
- **Line of fire:** `RangedAccuracy.HasLineOfFire`/`HasLineOfFireAtPosition` (a planned position means its cell), so a revealed enemy is targetable and anything targetable can be fired at. Bug spit range (`GridBugAttack`) follows. Weapons with `throughWalls` keep ignoring sight.
- **Cover:** tile cover is already grid-based. `RangedAccuracy.CoverAgainst`'s waist-height ray, which credits off-grid cover within 2.5 m of the target, is removed: layouts never place cover off the grid, and GridBattle's off-grid windmill still covers through its cells.
- **Head aim:** today "head visible" is its own ray, so a target behind low cover can expose its head and not its body. With grid sight the head and body are visible together. Head aim stays a weapon choice (accuracy and damage), not a sight question.
- **Molotov illumination:** `IlluminationSource` lights a position when grid sight reaches its cell from the source's cell.
- **What stays physics** (presentation only): projectile and laser visuals and impact points (`HeavyLaser`, planned shot points), mouse picking (`AimView`, the click plane) and `PlayerManualAim`.
- **Building collision** no longer decides sight, so the shop's glazing and Building_08's low openings stop mattering. The outline (walls, windows, doors) is the single truth.

*USB.*
- **`LayoutGrid.CanSee`** mirrors the rule, with the same line walk, corner rule and blockers (cells with Full cover, Full walls). `GridRules` gets a `GridSight` flag.
- **Tests.** NUnit for straight, diagonal and exact-corner lines, walls, windows and doors, symmetry, and Half/None blockers. NK's Round Trip compares `LayoutGrid.CanSee` with the game's rule on its 25 seeded maps.
- **Validators.**
  - The planned grid tier for metrics (§2 item 5, Phase 6) is exactly this rule.
  - The physics-tier `SIGHT_NO_OBSTACLE` warning loses its purpose; it becomes a note, "looks solid but blocks nothing", or goes.
  - `WALL_SIGHT_GAP` and `DOOR_COLLIDER` (W4) only matter if physics still decides something. They become notes, or go, for walled buildings.

*Harnesses (NK).*
- **Validate Paths:** unchanged.
- **Firing, bug, attack and illumination harnesses.** Validate Enemy Firing Positions, Validate Bug Attack Range, Validate Attack Alerts, Validate Illumination and Validate Ranged Accuracy build physical walls (`BoxCollider`s) for sight and fire. They switch to grid blockers: obstacle cells already mirror their walls, and the side-wall cases already use `BlockSide`. Their expectations are re-checked, especially head versus body aim behind low cover.
- **Validate Fog Of War** gets grid reveal cases: through a window, through a doorway, blocked by a wall, blocked at a building's corner, not blocked by Half cover.
- **New `Validate Grid Sight`:** the rule against an independently written line walk on random maps with obstacles and walls, symmetry, and the corner rule.
- **Reports:** Validate Layout Reports' GridBattle fixture loses its rock-set `SIGHT_NO_OBSTACLE` lines and is regenerated (`LayoutReportValidation.WriteGridBattleFixture`, diff reviewed).

*Sub-phases.*
- [ ] **S1 Rule (NK + USB):** `GridSight` on `GridMap` (from `PathNode.HasObstacle` with its cover, and `BlockedSides` with wall cover), the `LayoutGrid.CanSee` mirror, Validate Grid Sight and NUnit tests. Exit: both agree on the seeded maps; the independent walk agrees.
- [ ] **S2 Fog and spotting (NK):** reveal, `CanSeeUnit`/`CanFactionSeeUnit`, contact, threat memory, illumination. Exit: Validate Fog Of War, Attack Alerts and Illumination pass with grid cases. In CatalogTest Play Mode, the raider inside the shop is revealed and spotted through a window, and Building_08 reveals the outside through its windows and door.
- [ ] **S3 Line of fire and cover (NK):** `HasLineOfFire*`, bug spit, removing the waist ray, head aim. Exit: Ranged Accuracy, Enemy Firing Positions and Bug Attack Range pass with re-checked expectations. In Play Mode a unit inside the house shoots out of a window at a target it sees, and nothing behind a Full wall is targetable.
- [ ] **S4 Validators and docs (USB + NK):** `SIGHT_NO_OBSTACLE`, `WALL_SIGHT_GAP` and `DOOR_COLLIDER` downgraded or removed, the GridBattle report fixture regenerated, and `level-rules.md` (Line of sight, Cover), `combat-flows.md` (aiming, enemy turns) and `grid-layouts.md` updated.

*Risks.*
- **Gameplay change everywhere.** Fights in GridBattle change where sight relied on heights: thin Full walls block their whole cells, and heads no longer show over low cover on their own. Spot-check GridBattle in Play Mode after S3.
- **No step-out.** A unit beside a wall corner sees only along lines from its cell centre (no XCOM step-out); a later rule could add it.
- **Mismatched visuals.** A bullet's drawn path can still clip scenery the grid ignores (a lamp post, a rock); hits are decided by the rule.

**Phase 5: MCP tools.**
- [ ] `query_level_catalog` (filter by role and footprint, compact rows). It should also make the catalog findable (2026-10-17 gap): today an agent learns the path only from `generate_asset_catalog`'s description or a finished run's `jsonPath`, and a game project (NK) has no `level-catalog.json` at all. So: report which file it read; in a game project, list the reviewed pieces from `LevelCardSource` (overrides file + prefabs) instead of a catalog file. Until then NK `level-rules.md` § *Choosing pieces for a layout* and NK `AGENTS.md` point to AP's file by path.
- [ ] `get_layout`/`set_layout` (file-based, schema-validated) and `bake_layout`.
- [ ] `validate_layout` (grid tier) and `apply_layout` (and physics tier after apply).
- [ ] `render_layout_topdown` (orthographic capture of the board region, building on `get_screenshot`).
- [ ] All tools address objects by GUID or path, never by instance ID.
- [ ] **Wall runs with end caps** (deferred 2026-10-17). Synty builds walls from run pieces plus end pieces: the Apocalypse demo scene ends rows of `SM_Prop_Wall_Brick_01` with `SM_Prop_Wall_Brick_Pillar_01`. On the grid that needs a run's end cap to sit at the edge of the run's last cell (bleeding into it or sharing it) instead of taking a cell of its own, i.e. composite pieces or a placement rule from the wall-run `ends` sockets (§3 *Sockets*). Until then both are separate pieces: the brick wall a 2-cell run, the pillar a 1-cell column.
- [ ] **Dressed buildings from reference scenes** (agreed 2026-10-17; after 4c). Synty's modular buildings are plain out of the box, while the demo scenes dress them with windows, awnings, signs and rooftop units. Tool: the owner names a building instance in a demo scene; it collects the dressing props (children plus prefab instances overlapping the building's bounds, listed for confirmation since demo scenes place props as siblings), and saves a new prefab in AP (e.g. `Assets/_Composites/<Building>_Dressed.prefab`) with the building as root and the props as nested instances. The result is an ordinary prefab: it gets a card (footprint from the building's colliders), the gallery review and the GUID-preserving import. 4b uses the buildings as they are.

**Phase 6: Generation loop and metrics.**
- [ ] Metrics: cover density near enemy spawns, sightline length histogram (grid LOS), count of distinct flanking routes (edge-disjoint paths), open-ground crossings, deploy-to-enemy distance, and side symmetry.
- [ ] Calibrate on GridBattle and one or two maps you author.
- [ ] Later (larger maps): `GridManager` loads the per-level grid bake instead of relying on triggers (§2 item 2).
- [ ] `SKILL.md` playbook: plan ASCII, then JSON, then validate, then revise, then apply, then render, then report. Scale or size feedback on a piece goes into its card override followed by a reapply, never into the generated objects.

**Phase 7 (optional): bot playtests.** Run the existing `EnemyManager` AI against a scripted ally policy and record turns-to-contact and stalemates.

## 5. Risks found in the code

- **Trigger occupancy is lossy and order-dependent.** Any sliver overlap blocks a cell, the last obstacle wins a cell's cover, and obstacles never clear. Apply must inset colliders, and validators must report fit error.
- **Decoration silently blocks sight.** Any Default/Stencil collider blocks LOS, so grid-only reasoning will disagree with the game. The physics tier and the consistency validator exist for this reason.
- **The game already grants off-cell cover.** The 2.5 m waist raycast means misaligned props still matter. The baker and grid model ignore that bonus, so it is flagged as a known difference, not modelled.
- **The corner-cutting fix changes live gameplay.** Paths near walls get longer, which can shift AP spending and AI firing positions. The existing AP, firing-position and bug-range harnesses must be re-run, and enemy behaviour near walls spot-checked in Play Mode.
- **Board size from `LevelInfo` reorders startup.** `GridManager.Awake` must have the level before `CharacterLoader.Start`. Today the level reference lives only on `CharacterLoader`.
- **Thin ground truth.** There is one prototype map, so "fun" calibration needs maps you author first.
- **Scale mismatch.** Synty grid metrics may not divide into 2 m cells. Expect per-piece scale overrides (as Wall SF already has), and record them in cards.
- **Domain reloads** after C# edits drop the connection until the listener restarts (instance IDs survive them). Batch the C# edits per session and refresh once.
- **NK has no asmdefs.** Putting the adapter interface in USB Runtime avoids touching NK's assembly layout.

## 6. Decisions (2026-10-15)

- **D1 Packs:** PolygonApocalypse and PolygonApocalypseWasteland. Pieces move from AP to NK by GUID-preserving `.unitypackage` export.
- **D2 Corner-cutting:** fix it in the game (no diagonal step past an obstacle).
- **D3 Occupancy and authoring:** keep triggers for now. The scene stays the authoring source, and JSON is the agent's draft/interchange format with apply and bake in both directions. A runtime grid bake comes later for larger maps.
- **D4 Board size:** `LevelInfo.gridSize` drives the board.
- **D5 Interiors and elevation:** no interiors for now; elevation later. 2–3 storey buildings are allowed, with upper floors inaccessible. *(Interpretation; to be confirmed.)*
- **D6 GridBattle:** keep it as is (the Windmill is on the playable grid). Build and test in a new scene.
- **D7 Deploy cells:** move them into `LevelInfo`.
- **D8 Tests:** keep the existing NK harnesses. New pure validators in USB use NUnit (USB already has NUnit tests); new NK-side checks follow the harness style unless agreed otherwise.
- **D9 Layout JSON scope and sync (2026-10-16):** the JSON is the agent's working copy of a level, holding pieces, decor, deploy cells and (from Phase 3) enemy spawns. Apply writes it into the scene and the level asset; the scene and the level asset are the runtime sources of truth. When the JSON and the scene or level disagree, re-bake: the bake regenerates the JSON, so a stale JSON is never trusted or merged.
- **D10 Cell alignment strictness (2026-10-16):** blockers stay on whole cells. Later, for pieces such as bushes, relax it: decor may get free placement (sub-cell offset, any rotation), and a card may allow a blocker's visual to overhang its footprint while its collider is still fitted inside. Not part of Phase 3.
- **D11 "Apply", not "compile" (2026-10-16):** turning layout JSON into scene objects and level data is called *apply* (`LayoutApplier`, `apply_layout`, NK's **Apply LayoutTest Layout**), so it isn't mistaken for compiling code. Bake stays the reverse direction. Earlier entries in this plan use the new word; commits before this date say compile.
- **D12 Walls on cell sides (2026-10-18):** amends D5 for two buildings (Bld_Building_08, Bld_Shop_Medium_02): their ground floors become enterable. The game stays cell-based. Each cell stores, per side, the cover a wall gives and whether movement may cross it, and a wall is stored on both cells it separates. Straight steps across a wall are refused, and diagonals are refused past any walled side at the corner. Tile cover is the max of neighbour-obstacle cover and own-wall cover. Sight stays physics, and the wall data comes from a `GridWalls` component written by apply from the card outline. Freestanding barricades, fences and piles stay ordinary cell obstacles, and no modular-wall structures are planned. Upper floors stay inaccessible (a second grid level comes later). The owner's follow-up answers the same day: Building_08 at scale (1.15, 1, 1.15) and the shop at (1.1, 1, 1.1), so their walls sit on the grid lines; the shop keeps its boards and is entered by its back door (closed door disabled); windows give Half cover; the cutaway applies on hover; units cross doors through a door point (a child transform), visual only; the AI approaches by path distance. Design: Phase W.
- **D13 Grid sight (2026-10-19):** fog reveal, unit spotting and line of fire use one grid rule instead of physics rays: a supercover line between cell centres, blocked by cells with Full-cover obstacles and by Full-cover walls on cell sides. A line through an exact cell corner is blocked if either way round is blocked. Windows, doors, Half and movement-only obstacles, and units don't block. Scenery without a `GridObstacle` no longer blocks sight (the rock sets are play-area borders). Physics stays for presentation only (projectile visuals, mouse picking). Design: Phase S.

## 7. First milestone

A 12×12 board in the new `LayoutTest` scene (`gridSize` 12×12):
1. The agent queries the level catalog.
2. It writes a layout JSON with deploy and enemy cells.
3. Grid-tier validators pass.
4. `apply_layout` builds it under `[Generated Layout]`.
5. Baked occupancy equals the layout, and the physics tier passes.
6. `LayoutTest` runs, with units pathing around the new pieces (runtime occupancy assertion green).
7. A top-down render and a metrics report are produced.

Suggested session sizing: the USB port prerequisite is one short session, Phase 0 + 1 is one session in NK, Phase 2 is one to two in NK + USB, and Phase 3 + 5 is one to two. Phase 4 was sized as one session in AP; as of 2026-10-16 it is three (4a catalog code, 4b scan and owner review, 4c wire-up), with 4d as a short follow-up.
