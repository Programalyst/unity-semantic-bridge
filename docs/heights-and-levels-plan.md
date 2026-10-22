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

Builds on the H2 outline (D6-D8) and on H1. **H2a (the model), H2b (authoring), H2c
(presentation) and H2d (rules) are built (2026-10-21); H2e is not.** Owner decisions of 2026-10-21:

| # | Decision |
|---|---|
| D10 | A ladder costs the AP of **2 tiles** (4 AP for a soldier). |
| D11 | A shooter on a higher level than the target gets **+20 aim and +6 vision range** (D6: never within a level). The vision bonus is per observer and target, like the lit-target widening, and applies to the fog reveal too (owner, 2026-10-21; first +3). |
| D12 | **Sight and cover between levels follow the ordinary ground rules**: no seeing over Half cover from above. Kept simple because a target inside a building may be hidden by the floor or roof above. |
| D13 | **Overwatch fires only at the ladder ends** (the bottom and top cells), never mid-climb. |
| D14 | A shooter on a **lower level than its target gets -10 aim per level** it is below; cover rules stay the same (owner, 2026-10-21). |

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

- **Height bonus (D6, D11, D14).** Only when the shooter's level is above the target's: +20 aim, and +6 vision
  range for the observer on the higher level (per observer and target). Shooting up at a higher level: -10 aim per
  level below.
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

### H2b as built (2026-10-21)

- **Card.** A walled building's `walls` object takes `levels` (`[{"height": m, "cells": [[x0, y0, x1, y1]]}]`,
  level 1 first, height above the base the building stands on, cells inside the frame) and `ladders`
  (`[[x, y, level, x, y, level]]`, frame coordinates; a ground end may be outside the frame; the ends are the
  same cell or straight neighbours; an upper end must be a cell of its level). They rotate with the outline
  (`WallOutline.Levels`, `Ladders`, `WithLevels`) and so reach `PieceSpec.Outline`, `ConfigureWalls`,
  `TryReadWalls` and the baker unchanged. Parapets on an upper level are not modelled yet.
- **USB.** `LayoutGrid.Build` declares each level's cells (floor = its height) and the ladders (`LADDER_OFF_BOARD`
  when an end is off the board). The applier's `Floor Collider` gets one set of boxes per level, top at the
  base plus the level's height (`FLOOR_BOX ... level N` notes). The baker's `WALLS_DIFF` compares levels (cells,
  height within 1 cm) and ladders. `describe_level_cards` adds `measuredRoof`: the highest surface over each frame
  cell, from above the whole building.
- **NK.** `GridWalls` stores the levels and ladders in frame cells (`WallLevel`, `WallLadder`, `BaseY`);
  `NkGridRules.ConfigureWalls` writes them and `TryReadWalls` reads them back. `GridManager.RegisterWalls` then
  registers them (`RegisterLevels`: each level cell's floor from the H1 probe started at the level's nominal
  height, so it meets the level's floor box; then the ladders). The step rule now runs after the walls, so it
  covers the upper levels too.
- **Shop roof** (NK overrides, pending owner approval): level 1 at 5.157 m (`measuredRoof` median), 27 of its 32
  cells (the clutter cells (0,0), (1,0) at 6.07 m, (7,0), (0,3) and (7,3) left out), and one ladder up the back
  wall from (5,-1) to (5,0).
- **Verification.** NK *Validate Grid Levels* now 51 checks: CatalogTest applied in a temporary scene, the shop's
  `GridWalls` levels and roof floor boxes, registration on a `GridMap` (27 roof cells at the floor box's height, the
  ladder from the ground behind the shop, a path onto the roof for a soldier and none for a mega mutant), USB's grid
  and the baker reading the same. *Validate Floor Heights* tells ground floor boxes from roof boxes. USB tests:
  levels and ladders through JSON, rotation and validation, `Build`, the applier's roof boxes and the bake.
- **Scenes.** CatalogTest and RoadNetwork need the layout reapplied (or the shop replaced) before their shop has
  a roof: `ApplyFloors` builds floor boxes but does not rewrite `GridWalls`.

### H2c as built (2026-10-21)

- **Owner decisions:** the climb loops *Climbing Ladder* while the unit rises, then plays *Climbing To Top*;
  going down plays both backwards. The shop's ladder is its own `SM_Bld_Shop_Medium_02_Ladder_01` (so the card's
  ladder moved to `[6, -1, 0, 6, 0, 1, 0.51]`). Roofs are picked with an XCOM-style **view level** (E / Q, Page Up /
  Page Down, or the Up / Down buttons of a small HUD at the top right, below End Turn that `GridManager` builds when the board has upper
  levels, `GridViewLevelHud`; selecting a unit sets it to the unit's level).
- **Ladder offsets.** A ladder's optional 7th value is its offset along the side between its ends (towards +x on a
  ±y side, +y on a ±x side), turned like a door point (`LadderLink.Offset`, `Side`; NK `WallLadder.offset`,
  `GridWalls.FrameSidePoint`, `GridMap.SetLadderPoint`/`TryGetLadderPoint`).
- **Tiles.** `GridManager` spawns a tile per upper cell at its own floor (`GridTile.Level`, `Node`), so its trigger
  holds the units on that roof and its overlays sit on the roof; upper tiles draw no fog and reveal with the board
  cell under them (fog per level is H2d). Occupancy and obstacles are per level (`SetNodeOccupied`,
  `RemoveNodeOccupied`, `SetNodeObstacle` take a level); `GetGridTile(node)` and `GetNode(x, y, level)`.
- **Picking and the cutaway.** At view level 0 the pointer rays the click plane and a hovered building is cut away,
  as before; at 1 and up the pointer rays the `Walkable` floors (roof floor boxes included) and no building is cut.
  A building whose roof holds a unit is never cut (`GridManager.AnyUnitAbove`). Nodes come from positions by X/Z
  and the level whose floor is nearest the Y.
- **Outlines.** `ShowWalkableTiles(PathNode, ...)` draws one region per level the move reaches, each on its own
  floor (`GridOutline.MoveTiers(..., level)`, the renderer keyed by layer and level). The path preview breaks at a
  ladder and draws each level's piece at its own height. `GridMoveToCursor` and `GridCursor` sit on the tile's floor.
- **Moves.** `MoveAction` and `GridMovement.MoveToGrid` take a level; the player's clicks and the enemy planner pass
  it. `GridMovement` walks the node path; a ladder step runs `Climb`: walk to `ladderStandOff` (0.8 m by default: the shop's ladder stands 0.43 m out from the wall line) off the wall, face the wall,
  loop the climb rising at 0.47 m/s × `climbPace` (2.5 by default, after the owner found 1 and 1.75 too slow) to
  1.39 m below the floor above, then *Climbing To Top* while the root follows that clip's own root motion (1.39 m
  up, 1.06 m forward, sampled at tenths through the Animator): root motion is off on our units, so the clips play in
  place and the code moves the root. The owner set both clips' Root Transform Position (Y) to based upon the feet, so
  the feet stay on the root in both and the body matches at the switch (earlier imports needed a 1.05 m start offset
  and a 0.77 m step at the switch; re-measure if the imports change). Then a walk to the cell centre, where the ladder's 2 tiles are charged. The Humanoid controller has *Climb Ladder* and *Climb To Top* states with a `ClimbSpeed` speed
  parameter; the weapon is put away while climbing (`ClimbLoop`, `ClimbTop`, `EndClimb`).
- **Verification.** Harnesses: Grid Levels (the shop's ladder point on its ladder model, per-level outline regions
  from the roof), Paths, Action Points, Grid Outlines, Floor Heights, Firing Positions; USB tests for ladder
  offsets. **CatalogTest Play Mode** (layout reapplied in memory, not saved): Elena walked to the shop, climbed to
  the roof (about 12 s on the ladder and the top clip) and on to the next roof cell; her tile was the roof's; her
  movement outline covered roof and ground; she climbed back down (16 s) and walked off; Reno (power armor) got
  no path. Not seen: the climb on screen (the probe's camera did not follow), AP for a climb in combat (the moves
  ran while exploring). A second probe clicked like a player (select Elena, raise the view, click a roof cell through
  `ScreenToWorldClick`) and she climbed onto the roof.
- **Owner's first try (2026-10-21) and fixes.** The HUD moved below the End Turn button. The climb is faster by
  default (`climbPace` 2.5). The climb ends on the **ledge** at the ladder (the shop's parapet, about 0.9 m above its
  roof floor, found by a ray down at the wall line against scenery, at most 1.5 m above the floor) instead of the roof
  floor, so *Climbing To Top* no longer starts low and carries the unit into the wall; the walk on drops onto the
  roof. **Sight between levels** (D12, built here rather than in H2d because the raider inside the shop shot Elena on
  its roof): a cell under a floor or roof and a cell on that level or higher never see each other; on a line from a
  higher level, walls and obstacles under that level's cells don't block (it is above them); sight, step-outs, tile
  cover and fog reveal look up cells on the unit's level (`GridMap.GetNodeAtHeight`, `MapSight.NodeAt`).
- **Picking, revised (owner, 2026-10-21).** A building is cut away on hover only while an ally stands on its ground
  floor (so the cutaway no longer shows enemies inside), and the pointer always picks the nearest floor on the
  `Walkable` layer, skipping a cut building's floors, else the click plane: a roof is reached by clicking it. The
  view level (E / Q, Page Up / Page Down, the HUD's Up / Down) remains for a building with an ally inside. Fog: an
  observer reveals the cells it sees on every level, so a roof clears from the roof while the room under it stays
  hidden (the board tile's fog block under a revealed roof cell is lifted).
- **Next (owner's idea, not built):** a contextual climb button at the ladder (move to the ladder, then climb up or
  down), for a unit inside a building that needs to switch between the roof and the floor. **Seen once:** an enemy
  spotted Elena mid-climb and combat started, but she could not target it; the owner will try to reproduce it.
- **Known gaps for H2d:** enemy approach
  (`GetClosestWalkableGrid`) returns a cell without its level; the HUD is built in code, not yet part of the overlay.

### H2d as built (2026-10-21)

- **A unit's level** comes from its height, as for sight: `ISightGrid.LevelAt` (`MapSight`: the level whose floor is
  nearest under the position) and `GridVisibility.LevelsAbove(from, to)`. So a planned AI position counts at its own
  level, and a unit mid-climb at whichever floor it is nearer.
- **Aim (D11, D14).** `RangedAccuracy.HeightModifier(levelsAbove)`: +20 from any higher level, -10 per level from below,
  0 on one level. `CalculateChance` adds it with the other terms before the clamp (cover unchanged, focus still adds to a
  0% shot); `Preview` passes `LevelsAbove(shooter position, target)`, so the selector row, aim view, eligibility, overwatch
  and the AI forecasts agree.
- **Vision (D11).** `GridVisibility.HigherLevelVisionBonus` = 6 tiles, added in `CanSeeUnit` per observer and target after
  the lit-target widening (spotting, targeting, contact, overwatch). The fog reveal moved into
  `GridManager.CollectVisibleNodes` (static over a `GridMap`, so the harness runs it): each cell on each level is in range
  with the bonus when the observer's level is above the cell's; the scanned square grows by 6 from an upper level.
- **Overwatch (D13).** `GridMovement.IsOnLadder` is true from the first climb clip to the last (not the walk to the ladder
  or off it), and `GridVisibility.CanReactTo` refuses a target on a ladder. `TargetDetector` keeps polling the mover, so a
  shot comes at the bottom cell before the climb or the top cell after it. `OnTileReached` fires only at cell centres,
  which are the ladder ends, so the AI's stop-and-shoot is unchanged.
- **AI.** `GridManager.GetClosestWalkableGrid` (a `Vector2Int`) became `GetClosestWalkableNode` (the `PathNode`, level
  included); `EnemyManager` moves with its level and stays put when the node is its own. The firing planners weigh each
  reachable node at its own floor (`GridManager.GetWorldPosition(PathNode)`; they used `(x, y)` on the ground before,
  so a roof cell was judged as the cell under it). Reach and approach already took `GridUnit.CanUseLadder` (H2a).
- **Saves.** No mid-battle save stores a unit's cell (`PlayerData` is the roster and gear), so there is no level to add.
- **Verification.** Harnesses: *Validate Hit Chance* 189 (the modifier at +1, +2, -1, -2 levels, with cover, clamps and
  focus; previews from a roof, from two levels up and from below; a planned position on the roof; +6 vision from above,
  none upwards or on one roof; a 0.45 m kerb is no level; overwatch refused while on a ladder and allowed at its end),
  *Validate Grid Levels* 76 (the fog reveal with the bonus, as a 6-tile circle, never the room under the roof, none
  upwards or within the roof, none from a slab; `LevelAt`; the approach keeps the roof's level), *Validate Enemy Firing
  Positions* 31 (a climbing enemy takes the roof cell for 70% over 50% on the ground; one that can't climb stays down).
  Paths 2956, Action Points 80, Grid Sight 7715, Floor Heights 450, Attack Alerts 24, Illumination 22 and Bug Attack Range
  24 unchanged. **CatalogTest Play Mode** (in memory, nothing saved): Elena climbed to the shop roof (10,11@1); a raider
  placed 10 tiles off on open ground (2,5): she saw it (day vision 8, +6), it did not see her, combat started; her shot
  95% (75% on one level), its shot ineligible ("Not visible to squad"; it would be 30% - 10). Reversed, Elena on the
  ground (10,7) and the raider on the roof (9,11@1), 4.1 tiles: hers 65% (75 - 10), its 80% (60 + 20).
- **View level removed (owner, 2026-10-21).** With picking on the nearest floor and the climb button, the view level was
  no longer needed: `GridViewLevelHud`, its E / Q and Page Up / Page Down keys, `GridManager.ViewLevel` / `SetViewLevel`
  and the reset on selecting a unit are gone. The cutaway is now decided only by the cursor, a known unit inside and no
  unit on the roof.
- **Not done.** The AI does not yet value height for its own sake (a roof wins only through the hit chance), and the
  overwatch rule was checked in the harness, not by a live reaction shot at a ladder.

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

### Remaining work (owner, 2026-10-22)

Worked through one at a time:

- **H2e validators:** `TRANSITION_NOT_WALKABLE`, `LEVEL_UNREACHABLE`, `LADDER_ONLY`, `STEP_TOO_HIGH` per level; then
  the docs pass that closes H2.
- **Corner wall hugging** (after the step-out shot, NK `e3c3c44e`): an idle unit at a corner it can peek round leans
  on the wall, showing that it can step out. Clips: *Stand Cover To Look* first, *Cover Idle* as the alternative.
  *Built 2026-10-22 with Stand Cover To Look, waiting for the owner's look (NK level-rules.md, Line of sight).*
- **Aim view toggle:** a button while aiming that switches to the shoulder Aim Camera for a better look at the target,
  for any weapon (rather than tying the aim view to the sniper rifle).
- **RoadNetwork:** reapply its layout for the shop's roof, ladder and front doors.
- **Live checks still owed:** an overwatch shot at a unit on a ladder; a step-out to the left (the mirrored clip).
- **Later:** the AI valuing height for its own sake; the sniper rifle and Heavy Laser aiming at the planned point
  rather than the target's live one; stairs for the house's upper floor.

### Decisions (owner, 2026-10-22)

- **No power armor climbs ladders.** A jetpack power armor is planned instead; it will jet up to and down from roofs
  rather than climb, so the ladder rule stays on the unit type and power armor (`GridUnit.CanUseLadderWith`).
- **Parapets give no cover against shots from below.** They are rare on the current buildings; revisit if levels with
  many parapets appear.

## Open questions

- ~~**H1:** the exact overlay offset above the floor~~ answered in H1 as built: the overlays keep the offsets they
  were tuned at (outlines 0.13 m above the floor).
- **H1:** whether the grenade's arc, which starts from Y = 0 today, should start
  and land on floor heights or only land on them.
- ~~**H2:** how the climb clips make a climb, and whether some power armor may climb~~ answered: H2c as built, and
  no power armor climbs (owner, 2026-10-22; a jetpack armor will jet instead).
