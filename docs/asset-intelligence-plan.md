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

- **Layout shape.** Integer cells covering the whole board (`LevelInfo.gridSize`). There are four rotations (0–3 quarter turns). Pieces are anchored at their min corner, with the footprint taken from the catalog card after rotation. Per cell, the derived state is `{blocked, cover: None|Half|Full, occupant pieceId}`, with at most one blocking occupant per cell, which matches the game's one-obstacle-per-tile rule. Non-blocking decoration and ground pieces (roads, sidewalks) go in a separate layer that never blocks. There are no edge-based concepts: the game has none, and cell-based cover makes "between tiles" unrepresentable.
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
- **Sockets.** Reduced to a building `front` direction (door side) and wall-run `ends`. Name heuristics find door children; otherwise the override file supplies them. They matter only for the plausibility checks.
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
- [ ] Plausibility: road continuity in the ground layer, building fronts facing open or road cells. *Moved after Phase 4* (needs catalog roles).
- [ ] Scale: every placed piece uses its card's `placementScale` (or a declared per-piece exception), and its height falls in its role's range from `level-rules.md` (§3 *Piece scale*); report the measured height and a suggested scale. *Moved after Phase 4* (needs cards).
- [x] Fixtures: a bake of GridBattle (should pass; the Windmill is on the playable grid by design) plus deliberately broken JSON fixtures.
- [x] Tests: the pure validators in USB `Runtime/Layout/` get NUnit tests in `USB:Tests/Editor/`, run through `TestProject`. NK-side checks stay menu-item harnesses (decision D8).
- Exit: cell-addressed reports, and all fixtures behave as expected. **Done 2026-10-16** (plausibility and scale excepted, as scoped). How the validators work: [grid-layouts.md](grid-layouts.md#validators). USB: `LayoutValidator`/`LayoutReport` (pure) and `LayoutSceneValidator` (Editor, physics tier); 49 NUnit tests (`LayoutModelTests`, `LayoutValidatorTests` with 17 broken-layout fixtures) pass. NK: **Validate Layout Reports** (37 checks: applied LayoutTest gives a clean report; four broken JSON fixtures in `docs/fixtures/layouts/` report exactly their expected lines; GridBattle's report equals `docs/fixtures/gridbattle-layout-report.txt`), **Validate Layout Round Trip** (2,049, now with enemy spawns and a refused-spawn revert) and Validate Layout Baker (21) passed via `invoke_menu_item`. A live `bake_layout` of LayoutTest equals its JSON with no issues. In LayoutTest Play Mode a temporary probe (since removed) found 26 obstacle cells and cover equal to the layout on all 144 cells, and both raiders on their cells with their facings.
- Notes for later phases:
  - Reports are `CODE x,y message` lines with Error/Warning severity, errors first, capped at 40 lines by default, with an optional ASCII map (`A` deploy, `E` enemy, `-` unreachable). Warnings: `POCKET`, `FACING_OFF_AXIS`, `SIGHT_NO_OBSTACLE`, `DECOR_OUT_OF_BOUNDS`.
  - Levels store no spawn ids, so a bake names spawns `enemy1`, `enemy2`, … and `LayoutDocument.Diff` compares spawns by order, ignoring ids. Facing is a number in the JSON (no snapping); the encounter check wants 0–359 and warns off the eight grid directions.
  - For a hand-made scene, encounter and reachability run on the game's occupancy bake, since unrepresentable pieces still block. For an applied draft they run on the draft's footprints, and consistency compares those with the game's bake.
  - Sight colliders are found with an overlap box per cell from 0.5 to 2.5 m above the floor, grouped by prefab instance; a group is an obstacle when it holds a blocking collider or classifies as a blocker.
  - **Correction to the Phase 2 note:** since the Wall SF prefabs took GridBattle's scale, a GridBattle bake keeps its two walls as pieces. The report now lists the windmill (`OFFGRID_POSITION`, four `EXTRA_BLOCK` cells), the two rock sets (`OFFGRID_SCALE`, 2×) and the rock sets as `SIGHT_NO_OBSTACLE` (216 and 395 cells); every Desert 1 enemy is reachable.
  - A apply that throws after touching the scene (e.g. the adapter refusing a spawn GUID of the wrong type) now destroys its new root and reverts its Undo group.
  - The temporary-scene setup for NK harnesses is shared (`LayoutHarnessScene`).

**Phase 4: Level catalog (runs in AP; can start alongside Phase 1).**
- [ ] Extend `AssetCatalogGenerator`/`AssetCatalogRunner` with the card fields and the `level-catalog.json` output, reusing the batching and thumbnail pumping. Tests go in `USB:Tests/Editor/`.
- [ ] Scan both PolygonApocalypse and PolygonApocalypseWasteland. Heuristics plus an overrides file. Hand-review the top ~60 candidate pieces for the slice, including the road/sidewalk kit and 2–3 storey buildings.
- [ ] Scale gallery for that review (§3 *Piece scale*): a disposable generated scene in NK (no new Editor UI) with each candidate on the grid beside a soldier, captured top-down and from the side. The owner's approved scales go into the overrides file as `placementScale`. Seed suggestions from instance scales in the owner's hand-made scenes and from the role height ranges.
- [ ] Add the role height ranges to NK `level-rules.md` once the owner confirms them.
- [ ] Apply and bake read `placementScale` from the cards instead of using the prefab's own scale (and the measured-footprint stopgap from Phase 2 goes away).
- [ ] Export the chosen pieces as a `.unitypackage` into NK `Assets/3rd Party/…`, keeping GUIDs.

**Phase 5: MCP tools.**
- [ ] `query_level_catalog` (filter by role and footprint, compact rows).
- [ ] `get_layout`/`set_layout` (file-based, schema-validated) and `bake_layout`.
- [ ] `validate_layout` (grid tier) and `apply_layout` (and physics tier after apply).
- [ ] `render_layout_topdown` (orthographic capture of the board region, building on `get_screenshot`).
- [ ] All tools address objects by GUID or path, never by instance ID.

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

## 7. First milestone

A 12×12 board in the new `LayoutTest` scene (`gridSize` 12×12):
1. The agent queries the level catalog.
2. It writes a layout JSON with deploy and enemy cells.
3. Grid-tier validators pass.
4. `apply_layout` builds it under `[Generated Layout]`.
5. Baked occupancy equals the layout, and the physics tier passes.
6. `LayoutTest` runs, with units pathing around the new pieces (runtime occupancy assertion green).
7. A top-down render and a metrics report are produced.

Suggested session sizing: the USB port prerequisite is one short session, Phase 0 + 1 is one session in NK, Phase 4 is one in AP, Phase 2 is one to two in NK + USB, and Phase 3 + 5 is one to two.
