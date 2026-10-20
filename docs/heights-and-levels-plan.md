# Floor heights and grid levels: plan

Drafted October 20, 2026 with the owner, after the road kit made the flat board
visible: units stand at Y = 0, so their feet sink into roads (0.05 m) and
sidewalks (0.10 m), and the tile overlays (Cover Ground Quad at 0.02 m, Tile
Highlight at 0.04 m) hide under both. Nothing here is built yet.

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

## H2: grid levels (outline, needs its own plan)

- **Model (D7):** level 0 is today's board. Upper levels are sparse grids that
  exist only where a card declares one (a roof, an upper floor), each with its
  own `FloorY` per cell from H1. Nodes are addressed by (x, y, level).
- **Authoring:** a building card declares its levels next to `walls` (an ASCII
  outline per level and its height; parapets as wall sides). Ladders are
  transitions: a link between a cell on one level and a cell on another, either
  on the building's card or as a separate ladder piece with two anchor points.
- **Transitions:** graph edges with an AP cost and a climb animation. A unit
  whose `CanUseLadder` is false never gets a ladder edge (D8):
  `GridUnitInfo.canUseLadder` (true for soldiers and raiders, false for mega
  mutants), and, like `CanCrouch` today, `GridUnit.CanUseLadder` is false while
  power armor is equipped (the EVA suit is body armor with `isPowerArmor`, not a
  unit type). Pathfinding, the movement range, AI firing positions and the hover
  path preview all take the moving unit's ladder rule.
- **Rules:** height bonus to hit only when the shooter's level is above the
  target's (D6). Sight between levels needs a decision: e.g. whether a roof
  observer sees over Half cover and low walls on the level below, and how a roof
  edge blocks sight to cells right under it.
- **Systems that change:** per-level tile instances and pointer picking (rays hit
  the upper tiles first), fog per level, `BuildingCutaway` must not hide the roof
  a unit stands on, the camera, overwatch and spotting across levels, saves of a
  unit's level.
- **Validators:** a transition whose ends are not walkable, an upper level with
  no transition, a unit type that can reach an objective only by ladder.
- **What H1 gives H2:** `FloorY` per node on every level, the step rule within a
  level, ground-snap movement, the walkable layer and floor boxes.

## Open questions

- **H1:** the exact overlay offset above the floor (outlines use 0.13 m).
- **H1:** whether the grenade's arc, which starts from Y = 0 today, should start
  and land on floor heights or only land on them.
- **H2:** sight rules between levels; AP cost of a ladder; whether a unit on a
  ladder can be shot or interrupted (overwatch); whether some power armor (not
  the EVA suit) may climb, which would move the ladder rule onto the armor item.
