# Floor heights and grid levels: plan

Drafted October 20, 2026 with the owner, after the road kit made the flat board
visible: units stand at Y = 0, so their feet sink into roads (0.05 m) and
sidewalks (0.10 m), and the tile overlays (Cover Ground Quad at 0.02 m, Tile
Highlight at 0.04 m) hide under both. **H1 is built (2026-10-20, uncommitted until the owner's
review); see [H1 as built](#h1-as-built-2026-10-20).** H2 is not.

Two phases:

- **H1, floor heights (to build):** every cell has a floor height on one level.
  Units, paths, the cursor and the overlays follow it. It covers kerbs, raised
  slabs and building floors; slopes and stairs later reuse it.
- **H2, grid levels (outline):** upper grids such as a shop roof, joined to the
  ground by transitions such as ladders. The height bonus lives here.

Paths: NK code is under `nuclear-knights/Nuclear Knights/Assets/`, abbreviated
`NK:`. USB package code is under `com.gamenami.unity-semantic-bridge/`,
abbreviated `USB:`. The NK rules reference is `nuclear-knights/docs/level-rules.md`.

## Where things stand (2026-10-20)

| Thing | World Y | Set by |
|---|---|---|
| Desert Ground (built-in Plane, MeshCollider) | 0.00 | scene |
| Road tile top | 0.05 | `NkGridRules.GroundLift` 0.25 + card `lift` −0.2 |
| Sidewalk tile top (kerb 5 cm above the road) | 0.10 | `GroundLift` 0.25 + card `lift` −0.15 |
| Shop / house root | −0.15 / −0.09 | card `lift`; their floorboards are above 0 |
| Unit feet, path points, door points, cursor | 0.00 | `GridTile.FloorPosition`, `GridManager.GetWorldPosition`, door points, `GridCursor` (all hard-code 0) |
| Fog Stencil Mask Quad | 0.00 | `Grid Tile v2` prefab |
| Cover Ground Quad | 0.02 | prefab (local −0.495 × root scale 4) |
| Tile Highlight (tile Canvas) | 0.04 | prefab (local −0.49 × 4) |
| Cover icons (upright quads) | 0.10–1.10 | prefab |
| Movement and path outlines | 0.13 | `GridOutlineRenderer.height` |

Every placed road and sidewalk tile carries a non-trigger `BoxCollider` whose top
is the tile's surface (checked in RoadNetwork: road g2 top 0.05, sidewalk g3 top
0.10). The buildings carry one `MeshCollider` each, whose bounds top is the roof.

USB's `GridBoard.FloorY` (0 for NK) is the base plane the applier places pieces
on (`LayoutApplier`, `LayoutRender`, the sight-band validator). It stays the base;
per-cell heights are a separate query.

## Decisions (owner, 2026-10-20)

| # | Decision |
|---|---|
| D1 | Per-cell floor heights that unit movement follows, rather than lowering the road kit or raising the desert. The road kit's heights (2026-10-19) stay. |
| D2 | Units inside buildings stand on the building's floor (the shop and house floorboards sit above the ground). |
| D3 | A unit can step at most **0.5 m** between neighbouring cells on one level, deliberately steep for now. Climbing, slopes and stairs get their own discussion later. |
| D4 | Building floors get a **generated floor box**, not the building mesh on the walkable layer. |
| D5 | Outline ribbons stay at one height (the higher cell's floor along a shared edge) instead of following every step. |
| D6 | No height bonus within a level (a 0.5 m kerb or slab gives none). The bonus applies only for a full level up, in H2. |
| D7 | Upper levels are separate grids (e.g. the shop roof) with transition points between levels (e.g. a ladder's foot and top). |
| D8 | `GridUnitInfo` gets `canUseLadder`: mega mutants and EVA power armor suits do not climb ladders. |
| D9 | This plan lives in USB: layouts with several heights and levels will be common. |

### Why a raycast, not collider bounds

A road tile's box top is its surface, so bounds would work for the road kit. They
do not for anything else: a building's mesh bounds top is its roof; a ramp has a
different height at every point; a 4 m road tile covers 2 × 2 cells and a cell
can sit under several objects (road, sidewalk, desert, building floor). A
downward ray answers "what surface is under this point" for every shape and works
in hand-built scenes such as GridBattle. The ray starts low (the cell's base plus
the 0.5 m step, about 0.6 m) so it never meets a roof first.

## H1: floor heights on one level

### H1a. Walkable surfaces

- **NK:** add a `Walkable` physics layer. Desert Ground (GridBattle, CatalogTest,
  RoadNetwork, the calibration scenes) moves to it.
- **USB applier:** ground-role pieces go on the adapter's walkable layer (new
  `IGridRulesAdapter.WalkableLayer`, -1 to leave layers alone). Their box
  colliders already sit at the surface.
- **Building floor box (D4):** for a building card with `walls`, the applier adds
  one `Floor Collider` child: a thin box (5 cm) over the interior cells of the
  walls outline, top at the card's new `floorHeight` (metres above the placed
  base, measured once per building and approved in `LevelCatalogOverrides.json`
  like `lift`), on the walkable layer, no `GridObstacle`. The building mesh keeps
  its layer, so aiming, bullets and the cutaway are unchanged.
- **Card tooling:** `describe_level_cards` reports a measured floor height for a
  building (a ray down at each interior cell centre against its mesh) so the
  override can be filled in from the card review.
- Check before building: which layer masks `ScreenToWorldClick`, `AimView`,
  `BuildingCutaway` and bullets use, so the new layer does not change what they
  hit.

### H1b. Floor height in the grid

- `PathNode.FloorY`, sampled once by `GridManager` after tiles exist: a ray down
  from (cell centre, base + 0.6 m) for 1.5 m against `Walkable` only, triggers
  ignored. A miss gives the base (0) and one warning listing the cells.
- `GridTile.FloorPosition` returns `(x, FloorY, z)`. `GridManager.GetWorldPosition`
  with a node, and `GetWorldPositionPath`, use the node's `FloorY`.
- **Step rule (D3):** in `GridMap`, the edge between two neighbours whose floors
  differ by more than 0.5 m is closed to movement, like a Full wall side for
  movement only; sight is unchanged. Diagonals follow the existing corner rule.

### H1c. What follows the floor

- Units: spawn and deploy (`CharacterLoader`), path points, door points (the
  higher of the two cells), `GridCursor`, ground fire and the chem splash (both
  already use `FloorPosition`), the grenade's landing.
- **Movement:** `GridMovement` keeps moving in X/Z and takes Y each frame from a
  ray straight down against `Walkable`, starting 0.5 m above the unit's current
  Y. Kerbs become a step, building thresholds work, and later slopes need no new
  code. One ray per moving unit per frame.
- **Tile overlays:** each tile's Canvas (Tile Highlight) and Cover Ground Quad
  rise by its `FloorY` plus a small offset so they sit above every surface; cover
  icons move with them.
- **Outlines (D5):** a ribbon edge sits at the higher floor of the cells it
  borders plus 0.13 m; a path segment at the higher of its two cells.

### H1d. USB and validators

- `IGridRulesAdapter` gains a per-cell floor query for a scene (NK samples the
  same way as the runtime), so validators and metrics can read heights. Layout
  JSON does not store heights in H1; they come from the pieces.
- New validator `STEP_TOO_HIGH` (error): two walkable neighbours more than 0.5 m
  apart with no wall between them. It would also catch a building whose
  `floorHeight` is off.
- `ROUTES` and `COVER_SPACING` use the step rule through the same graph.
- Docs: `grid-layouts.md` (applier, validators), NK `level-rules.md` (*Grid
  geometry*, *Piece heights by role*), NK `combat-flows.md` (movement).

### H1e. Verification

- **Validate Floor Heights** (NK harness): in CatalogTest, the sampled `FloorY`
  of a road cell is 0.05, a sidewalk cell 0.10, a desert cell 0, a building
  interior its card's `floorHeight`; a synthetic 0.6 m step closes the edge and a
  0.5 m one does not.
- USB tests: ground pieces on the walkable layer, the floor box's cells and
  height, `STEP_TOO_HIGH`.
- RoadNetwork Play Mode: units on desert, road, sidewalk and inside the shop;
  a move across a kerb and through the shop door; cover icons and highlight
  visible on every surface; GridBattle unchanged.

## H1 as built (2026-10-20)

- **Layer check first.** `ScreenToWorldClick` and `BuildingCutaway` ray the click plane only;
  `AimView`'s pointer mask was Units, RagDoll, Player, Default, Stencil, and now has `Walkable` too, so
  the ground still stops the pointer; `TargetDetector`'s and `GridSniperRifle`'s Default masks are
  declared but unused; bullets are physical and meet the ground through the collision matrix
  (Projectile and RagDoll collide with Default). So `Walkable` (NK slot 17; 15 carries stale collision
  bits) collides like Default except Grid Tiles (tile triggers and the occupancy baker never see floors).
- **Not in the plan: rendering.** No free slot was in both the URP renderer's opaque mask and the main
  camera's culling mask, so a renderer moved to a new layer vanished. NK's renderer masks and each
  battle scene's main camera now include `Walkable` (NK menu *Set Up Walkable Layer*, *Set Up Floor
  Heights In Open Scene*). GridBattle was updated on disk; RoadNetwork in memory only (not saved);
  CatalogTest and the two calibration scenes still need the scene menu.
- **USB:** `IGridRulesAdapter.WalkableLayer` and `FloorHeights(scene, board)`, `GridRules.MaxStepHeight`,
  `LayoutGrid.SetFloors`/`FloorAt`/`IsStepClosed` (the step rule in `CanStep`), `LayoutValidator.Steps`
  (`STEP_TOO_HIGH`), `WallOutline.InteriorCells`, the card's `floorHeight`, the applier's ground layer and
  `Floor Collider`, `LayoutApplier.ApplyFloors` for scenes applied before, and `describe_level_cards`'s
  `measuredFloor`. The floor box's top is the instance's root less the card's lift plus `floorHeight`, so
  it follows a building placed off its lift (RoadNetwork's shop and house roots sit at 0, not at their lifts).
- **Floor heights measured:** at their lifts the house's floor is 0.000 m and the shop's −0.004 m
  (median); the brief's "floors above 0" was RoadNetwork's unlifted placement (0.09 and 0.146 m there).
  Both are in NK's overrides, pending the owner's approval.
- **Open questions answered:** the overlays keep the offsets they were tuned at over the desert (no extra
  offset: Canvas 0.04 m, Cover Ground Quad 0.05 m above the floor). Throws (grenade, Molotov, bug spit)
  land on the target cell's floor; their arc starts from the thrower's hand as before.
- **Verification:** NK *Validate Floor Heights* 450 checks; Paths, Grid Outlines, Grid Sight, Layout Round
  Trip (4110), Layout (21), Layout Reports (95), Enemy Firing Positions (28) and Illumination (22) pass;
  USB EditMode 318 passed, 0 failed. RoadNetwork Play Mode: raiders on the shop (0.146), house (0.09) and
  road (0.05) floors, the squad on desert (0); Reno desert → road → across the kerb (0.05 → 0.10) and
  through the shop door (0 → 0.146) with feet on the floor every frame; sidewalk tile Canvas at 0.14,
  cursor 0.10; all 840 outline vertices at least 0.13 m above their floor. GridBattle's Play Mode check
  was not run (it would close RoadNetwork).

## H2: grid levels (draft plan, 2026-10-21)

Builds on the H2 outline (D6-D8) and on H1. **H2a (the model) is built (2026-10-21); the rest is
not.** Owner decisions of 2026-10-21:

| # | Decision |
|---|---|
| D10 | A ladder costs the AP of **2 tiles** (4 AP for a soldier). |
| D11 | A shooter on a higher level than the target gets **+20 aim and +3 vision range** (D6: never within a level). |
| D12 | **Sight and cover between levels follow the ordinary ground rules**: no seeing over Half cover from above. Kept simple because a target inside a building may be hidden by the floor or roof above. |
| D13 | **Overwatch fires only at the ladder ends** (the bottom and top cells), never mid-climb. |

The climb: the owner added three Mixamo-style humanoid clips to NK on 2026-10-21
(`Assets/Animations/Humanoid/Characters@Climbing.fbx`, `Characters@Climbing Ladder.fbx`,
`Characters@Climbing To Top.fbx`); how to use them is decided in H2c. No placeholder climb.

### What exists to build on (survey, 2026-10-21)

- **Geometry.** RoadNetwork's house (SM_Bld_Building_08, two storeys) has an upper
  floor at about 3.09 m and its roof at about 6.0 m. The shop (one tall storey) has a roof at about
  5.2-5.3 m. Measured with the H1 probe over the visible meshes.
- **Content gaps.** No climb animation in any NK Animator controller. No ladder in the
  Apocalypse packs; PolygonSciFiSpace has `SM_Prop_Ladder_01` and SciFiWorlds a crate-building ladder.
- **One flat board everywhere.** `GridManager` holds one `GridMap` and one `GridTile[,]`. The
  pointer (`ScreenToWorldClick`, `GridPathPreview`, `BuildingCutaway`) rays a click plane at Y = 0.
  The fog texture (`GridFogOfWarMask`) is one byte per (x, y). Units store `GridPosition` as a
  `PathNode` (13 files use it, 9 use `GetGridTile`).

### Model (D7)

- **One graph, levelled nodes.** `PathNode` gets `Level` (0 = today's board). `GridMap` keeps
  level 0 dense and stores each upper level sparsely: only the cells a card declares. `GetNode(x, y,
  level = 0)` keeps every current caller working on level 0.
- **Neighbours.** Neighbours within a level follow today's rules plus H1's step rule. A level's
  outer edge is closed unless the level says otherwise. A **transition** (ladder) adds an edge
  between a cell on one level and a cell on another. `FindPath`, `GetReachableNodeSteps`,
  `GetStepDistancesFrom` and the AI planners already walk `NeighborNodes`, so they cross levels
  without new search code.
- **Cost.** A ladder edge costs 2 entered tiles of AP (D10). A* adds the same cost to `GCost`,
  and `GridMovement` charges it on reaching the top or bottom.
- **Ladder rule (D8).** `GridUnitInfo.canUseLadder` (soldiers and raiders true, mega mutants
  false). `GridUnit.CanUseLadder` is false while power armor is equipped, like `CanCrouch`. Path
  and reach take the moving unit, so a ladder edge is skipped for a unit that can't climb. The
  movement outline, path preview and AI firing positions use the same call.
- **USB.** `LayoutGrid` gets the same model (levels, transitions, `CanStep` across a transition
  only with the unit's ladder rule), so reachability, metrics and validators see it.

### Authoring

- **Card.** A walled building's overrides entry gets `levels`: per upper level, its height above
  the base (like `floorHeight`), an ASCII outline in the `walls` format (parapets and roof edges
  as wall sides) and its cell rectangle in frame coordinates. A `ladders` list gives
  `[[x, y, level], [x, y, level], side]`: the two ends and the wall side the ladder stands on.
  Ladder ends may be outside the frame (a ladder up the outside wall).
- **Apply.** Per level, the applier builds a floor box on `Walkable`, as for H1's ground floor,
  and places the ladder model (a catalog piece named by the card, or a placeholder box) on its side.
  `GridLevels` (NK, next to `GridWalls`) records the levels and ladders for `GridManager`. The
  baker reads them back.
- **First content.** The shop roof (level 1) with one ladder up the back wall. The house's upper
  floor would need stairs, which wait for the stairs discussion.

### Presentation

- **Tiles.** Upper-level tiles are created only for the declared cells. Their Canvas, cover
  ground quad and cover icons sit on their own floor.
- **Picking.** The pointer rays the `Walkable` layer first: a hit on an upper level's floor box
  picks that level's cell. Otherwise it falls back to the click plane, as now. H1's floor boxes are
  the picking surfaces.
- **Cutaway.** `BuildingCutaway` does not hide a roof that a unit stands on or that the cursor is
  over.
- **Outlines.** One region per level, each at its own floor, as in H1.
- **Climb.** The unit faces the wall and climbs between the ladder ends with the owner's climb
  clips (above); the sequence (loop, climb to top, getting on at the top for a descent) is decided in H2c.
- **Camera.** It follows a climbing unit like a walking one.

### Rules

- **Height bonus (D6, D11).** Only when the shooter's level is above the target's: +20 aim, and
  +3 vision range for the observer on the higher level.
- **Sight between levels (D12).** The ordinary grid sight and cover rules, on the 2D line between the
  two cells, whatever their levels. One addition, so floors and roofs hide what is under them: a
  cell inside a building's frame on a lower level and a cell on that building's upper level (or
  roof) never see each other.
- **Fog.** Per level: one fog texture per level, or a revealed flag per upper node with only
  level 0 drawn as fog. Proposed: fog draws level 0; an upper node counts as revealed for spotting
  only.
- **Overwatch on ladders (D13).** The ladder ends are ordinary cells, so overwatch fires at the
  bottom or top cell, never mid-climb.
- **AI.** Enemies can climb when their unit type can (D8). Firing positions across levels use
  the level-aware sight rule.
- **Saves.** A unit's level is stored with its cell.

### Validators

- `TRANSITION_NOT_WALKABLE`: a ladder end on a blocked, missing or off-level cell (error).
- `LEVEL_UNREACHABLE`: an upper level no ladder reaches (warning).
- `LADDER_ONLY`: an enemy or objective that a non-climbing unit type can reach only by
  ladder (warning).
- `STEP_TOO_HIGH` per level.

### H2a as built (2026-10-21)

- **NK `GridMap`:** `PathNode.Level`; `AddUpperCell(x, y, level, floorY)` declares an upper cell, joined to
  the declared cells around it; `AddLadder(a, b)` with `IsLadder` and `StepTiles` (`LadderTiles` = 2);
  `GetNode(x, y, level)` (`GetNode(x, y)` is level 0, so existing callers are unchanged); `AllNodes`,
  `UpperNodes`. `CanStep(from, to, canUseLadder)` allows a ladder only with the rule, keeps other steps on
  one level, and refuses a diagonal past a missing cell at an upper level's edge. `FindPath`,
  `GetReachableNodeSteps` (now a bucket search, still in nondecreasing order), `GetStepDistances` (every
  level; `GetStepDistancesFrom` keeps its level-0 array) and `FindClosestReachableNode` take
  `canUseLadder`. The step rule and `BlockSide` cover upper levels.
- **NK units:** `GridUnitInfo.canUseLadder` (default true; Mega Mutant Heavy false) and
  `GridUnit.CanUseLadder` (`CanUseLadderWith`: the type's rule, never in power armor). Movement, the
  movement outline, the path preview and the enemy planners pass it.
- **NK `GridManager`:** `GetNodeFromWorldPos` picks the level whose floor is nearest the position's Y;
  `GetWorldPosition(PathNode)`; path points on each node's own floor. The movement outline still shows
  only the start's level (H2c), and nothing declares upper levels in a scene yet (H2b).
- **USB `LayoutGrid`:** `LevelCell`, `AddUpperCell`, `AddLadder`, `Exists`, `FloorAt(LevelCell)`,
  `CanStep(LevelCell, LevelCell, canUseLadder)`, `Neighbours` with tile costs and a weighted `StepsFrom`;
  `GridRules.LadderTiles` (NK 2). The validators don't use levels yet (H2e).
- **Verification:** NK *Validate Grid Levels* (40 checks: model, ladder paths and costs, reach, approach,
  the upper step rule, the unit rule with real unit and armor assets, and `LayoutGrid` agreeing with
  `GridMap`); Paths, Action Points, Grid Outlines, Grid Sight, Floor Heights, Firing Positions, Bug Attack
  Range, Attack Alerts, Layout Round Trip and Reports unchanged; USB `GridLevelTests`.
- **For the owner:** only Mega Mutant Heavy is set not to climb. War Robot, BR Base, Rook, Spitter Bug and
  Plasma Stalker climb by default until told otherwise.

### Phases

1. **H2a, model:** `PathNode.Level`, sparse upper levels, ladder edges with cost and the
   ladder rule, in `GridMap` and USB's `LayoutGrid`. Harness: paths over a ladder, cost, a
   mega mutant and power armor refused.
2. **H2b, authoring:** card `levels` and `ladders`, applier floor boxes and ladder pieces,
   `GridLevels`, baker. USB tests and the shop-roof card.
3. **H2c, presentation:** upper tiles, picking, cutaway, outlines, the climb placeholder.
   RoadNetwork Play Mode: a soldier climbs to the shop roof; Bishop armor cannot.
4. **H2d, rules:** height bonus, level-aware sight and fog, overwatch, AI and saves.
5. **H2e, validators and docs.**

## Open questions

- **H1:** the exact overlay offset above the floor (outlines use 0.13 m).
- **H1:** whether the grenade's arc, which starts from Y = 0 today, should start
  and land on floor heights or only land on them.
- **H2:** how the three climb clips make a climb up and down (H2c). Also whether some
  power armor (not the EVA suit) may climb, which would move the ladder rule onto
  the armor item.
