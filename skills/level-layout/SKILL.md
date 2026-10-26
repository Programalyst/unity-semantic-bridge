---
name: level-layout
description: Build or change a grid-based tactical level in a Unity project through the Unity Semantic Bridge (USB) layout tools. Use when asked to make a new level or map, lay out cover, buildings, roads or enemy spawns on a grid, change an existing layout, or act on feedback about a level ("too open", "walls too tall", "move the raider"). Covers the loop of ASCII plan, layout JSON, validation, apply, top-down render and report.
---

# Level layout with the USB tools

You build levels as a **layout draft** (compact JSON), check it with the validators, apply it
into a level scene, look at it from above, and report. The scene and the level asset are the
runtime truth; the JSON is your working copy. USB is generic: the game supplies the rules (cell
size, movement, cover, sight) through its grid-rules adapter, and its own rules doc says what
they are.

## 0. Before you start

- **Read the game's rules first.** The project's `AGENTS.md` names its level rules doc and
  template scene (Nuclear Knights: `docs/level-rules.md`, template
  `Assets/Scenes/CatalogTest.unity`). Know the cell size, the movement rule (8-way? corner
  cutting?), which side cover counts, what blocks sight, and the cover height bands.
- **Get the brief.** Board size, enemy count and kinds, theme, buildings, where the squad starts,
  and what kind of fight it should be (approach across open ground, street fight, assault on a
  building). If the brief leaves one of these open and it matters, ask; otherwise pick and say so.
- **Driving the Editor.** Check `poll_play_state` → `interactionMode`; if it is not
  `no_throttling`, call `set_editor_throttling("no_throttling")` and restore with
  `set_editor_throttling("restore")` when you are done. After editing project files outside
  Unity, call `refresh_assets`, then poll `get_compilation_status` until it is not `PENDING`.
- **Scenes you did not create are not yours.** Never save, discard, close or apply into them.
  The owner may have unsaved changes in any open scene. `open_scene` refuses when the active scene
  is dirty: tell the owner instead of forcing it. The layout tools never open, save or close a
  scene; apply leaves the scene dirty for the owner to save.

## 1. Find pieces

- `query_level_catalog` lists the pieces you may use (in a game project: the reviewed pieces, from
  the project's `AssetCatalog/LevelCatalogOverrides.json`). Filter with `role` (`cover_half`,
  `cover_full`, `blocker`, `building`, `ground`, `decor`), `cover`, `max_cells`/`min_cells` and
  `name`. Each row: GUID, role, cover, cells at rot 0 (`WxH`), height, scale, id, prefab path.
  `MISSING` rows at the end are stale entries; don't use them.
- `describe_level_cards(paths=[...])` gives a piece's full card when its footprint, front, door
  or lift matters (buildings especially: `walls` outline, door sides, `front`, and for a `+roof`
  building its `levels` and `ladders`).
- The role column says `+walls` for a building you can enter and `+roof` when a ladder leads to its
  roof (see *Heights, roofs and ladders* below).
- **Enemies** are `[localId, profileGuid, visualGuid, x, y, facing]`. Take the profile and visual
  from an existing layout (`get_layout` on the template's layout file, which the game's rules doc
  names; NK: `Assets/Settings/Levels/CatalogTest.layout.json`) or the game's docs; asset paths are
  accepted where GUIDs go.
- **Deploy cells:** the rules doc says how many the game needs (NK spawns one saved squad member
  per cell, three today, and an empty list fails).
- Pieces without a reviewed card can still be used (measured on an instance), but prefer carded
  pieces: their scale and footprint are owner-approved.

## 2. Plan in ASCII

Draw the board before writing JSON, top row = highest y, x to the right, in the validators'
legend: `#` full cover, `h` half, `o` movement-only blocker, `.` open, `A` deploy, `E` enemy,
`L` a ladder's foot, `^` open ground under a roof, and for your plan only `=` road or other ground decor and `B` a building's frame (the
validators' map prints buildings with their walls at double resolution instead). Beside it, list each piece with its
anchor and rotation. Check by eye: a way from the deploy cells to every enemy, cover within a
move of where the squad will want to stop, no sealed pockets, buildings with their door or front
towards open ground. This is cheap to revise and to show the owner.

## 3. Write the JSON and read the grid report

```json
{"v":1,"size":[16,16],
 "pieces":[["sandbags1","<guid or Assets/...prefab>",6,4,0]],
 "deploy":[[1,1],[2,1],[1,2]],
 "enemies":[["raider1","<profile guid or path>","<visual guid or path>",10,11,180]],
 "decor":[["road1","<guid>",0,8,0]]}
```

- Each piece is `[localId, prefab, x, y, rot]`. `(x, y)` is the **min corner of the footprint
  after rotation**; rot is 0–3 clockwise quarter turns **on top of the prefab root's own
  rotation**. A `2x1` piece at rot 1 covers 1 × 2 cells from its anchor.
- `pieces` block movement and give cover; `decor` never blocks (roads, sidewalks, small props).
  Ground and decor cards go in `decor`, cover in `pieces` (`ROLE_LAYER` otherwise).
- `size` must equal the level's board (`create_layout_scene` sets it, step 5).
- Facing is degrees, 0 = +y, 90 = +x; keep to the eight grid directions.
- `set_layout(path=..., layout=...)` writes the canonical file (prefab paths become GUIDs) and
  returns the **grid tier** report. Put the file beside the level, e.g.
  `Assets/Settings/Levels/<Name>.layout.json`. `ascii=true` adds the map; for a board with walled
  buildings it is drawn at double resolution with each building's **rotated** outline (`-`/`|`
  wall, `w` window, `D` door), which is the easy way to check which way its door and windows face.

## 4. Revise until clean

The report is `LAYOUT_REPORT n errors, m warnings`, then `CODE x,y message` lines. Fix every
error. Every warning left must be one you intend and can explain. The ones that usually matter:

| Code | Means | Usual fix |
|---|---|---|
| `OVERLAP`, `OUT_OF_BOUNDS` | footprints collide or leave the board | recompute anchors after rotation |
| `ENEMY_UNREACHABLE`, `POCKET` | the squad can't walk there | open a gap; diagonals don't pass blocked corners |
| `DEPLOY_BLOCKED`, `ENEMY_BLOCKED`, `CELL_SHARED` | a unit starts on a blocker or another unit | move the unit |
| `WALL_CROSSES_PIECE` | a piece straddles a building wall | move it to one side |
| `DOOR_BLOCKED`, `DOOR_OFF_BOARD`, `BUILDING_SEALED` | a building can't be entered | turn it or clear the cell outside its door |
| `BUILDING_FRONT` | a building faces the edge or only blockers | turn it towards open ground or a road |
| `GROUND_RUN`, `GROUND_OVERLAP` | a road run stops mid-board, or tiles stack | run roads to the board edge; one ground tile per cell |
| `HEIGHT_BAND` (error) | a piece's height is outside its cover band | use another piece, or raise it with the owner (card override) |
| `STEP_TOO_HIGH` (error) | two walkable neighbours (on the ground or on one roof) differ by more than the game's step | a card's floor or roof height is off: report it |
| `TRANSITION_NOT_WALKABLE` (error) | a ladder's foot is under a piece, or a ladder end is off the board | clear the `L` cell; move the building so its ladder stays on the board |
| `LEVEL_UNREACHABLE` | a roof no ladder from the ground reaches | as above, or accept it and say so |
| `LADDER_ONLY` | an enemy the squad reaches only over a roof | open a way on the ground; units that can't climb never get there |
| `FACING_OFF_AXIS` | facing between grid directions | use a multiple of 45 |

Notes are for the record (owner-approved content outside a rule); leave them.

Before the numbers, check the level has a reason to exist: a theme the enemies belong to (a building
they hold, a compound, a road), with the enemies in and around it, some cover where the squad deploys, and
no closed-off area the squad must walk into to find nothing. Owners grade empty maps down even when they play.

Then judge the fight with `validate_layout(path=..., metrics_only=true)`: per enemy the distance in turns
(`DISTANCE`), cover near it facing the squad (`COVER_NEAR`), separate ways to reach it (`ROUTES`), the longest
dash through open ground it can see (`OPEN_GROUND`) and the longest stretch without cover on the way (`COVER_SPACING`); cover near the deploy cells; how far sight carries across the
map (`SIGHTLINES`). If the game sets targets, lines outside them come first: revise for those. Without targets, say
in your report what the numbers mean for the fight (e.g. "one route to the shop raider, through its back door"). Revise with
`set_layout` again, or `validate_layout(path=...)` after editing the file yourself.

## Heights, roofs and ladders

Some buildings have a roof units can stand on, reached by a ladder (`+roof` in the catalog; NK: the
shop, `SM_Bld_Shop_Medium_02`). The roof and its ladder come from the building's card: you place the
building and the roof comes with it. The layout has no height fields.

- **Where they land.** With `ascii=true` the report marks roof cells `^` (open ground under the roof)
  and each ladder's foot `L`. Keep the `L` cell and a cell beside it open, and keep the foot on the
  board. The foot is often outside the frame, behind the building.
- **What a roof does in the fight** is the game's rule; read its rules doc. NK: a shooter on a higher
  level gets +20 aim and +6 tiles of sight against targets below, and -10 aim per level when shooting
  up; a ladder costs 2 tiles of movement; mega mutants and power armor can't climb, and the boost-jump
  suit can jump onto a roof.
- **The metrics don't see height.** `DISTANCE`, `ROUTES`, `COVER_SPACING` and `SIGHTLINES` are
  measured on the ground only. Judge a roof yourself: what it overlooks within sight + 6, whether the
  squad or the enemy will reach it first, and say so in your report.
- **Enemies start on the ground.** Layout spawns have no level, so an enemy can't start on a roof
  yet. Put enemies inside or around the building; one may climb up during the fight.
- Units always stand on the floor under them: kerbs, sidewalks and building floors are handled by the
  game. A step taller than the game allows is `STEP_TOO_HIGH`.

## 5. Build the level scene and apply

- **A new level:** `create_layout_scene(template=<the game's template scene>, path="Assets/Scenes/<Name>.unity", size=[w, h])`.
  It copies the scene and its level asset and repoints the copy, so applying never rewrites the
  template's level. It copies the template's file, so it refuses while the template is open with
  unsaved changes (Unity often marks a scene dirty after Play Mode): ask the owner to save it, or use
  another level scene that isn't open as the template. Then `open_scene` it. It refuses while the active scene has unsaved changes:
  if that is a scene you created and applied earlier, ask the owner whether to save it; otherwise
  tell the owner and wait. Never apply into the template itself or into a scene you did not create unless
  the owner asks.
- `apply_layout(path=..., scene=...)` builds the draft under `[Generated Layout]`, writes deploy
  cells and spawns into the level, and returns the **full report** (grid tier plus the game's own
  occupancy). `FIT_*` lines are notes about collider fitting; `MISSING_BLOCK`/`EXTRA_BLOCK`/`COVER`
  mean the game disagrees with the layout: report them. It is one Undo group and is not saved.
- Saving is the owner's call. For a scene you created, saving it is fine when the owner wants to
  keep or play it; say that you did.

## 6. Look at it

- `render_layout_topdown(scene=...)`: straight down, grid lines (every 5th yellow, board edge red,
  cell 0,0 a cyan L), deploy cells blue, enemies red. Image right is +x, up is +y, like the ASCII
  map. Add `cut_height=2.4` (or the game's value) to see inside buildings.
- Compare it with your ASCII plan: pieces in their cells, buildings facing the right way, roads
  lined up. Ground cards have no front, so check sidewalk kerbs here: if the kerb faces away from
  the road, turn the run by two quarter turns. Pieces that look wrong for their size are feedback for their card, not the layout.

## 7. Report

Give the owner: the brief as you understood it, the ASCII map, the final report (errors,
warnings you kept and why), the render(s), the files and scene you created, and what is left or
uncertain. Keep it short; the render and the map carry most of it.

## Feedback and changes

- **"This piece is too big / tall / small":** a piece looks the same in every level, so change
  its card, not the scene: its entry in the project's `AssetCatalog/LevelCatalogOverrides.json`
  (`placementScale`, `lift`, `footprint`), then `refresh_assets` and **reapply every layout that
  uses it**. Never fix the generated objects by hand; the next apply replaces them.
- **"Move / add / remove this":** edit the JSON (`set_layout`), validate, reapply.
- **The owner edited the scene:** `bake_layout(scene=..., path=...)` reads it back into the JSON;
  the scene wins. Off-grid or off-scale objects are reported, not snapped: ask before changing them.
- **A rule seems wrong** (cover, sight, movement, heights): it is the game's, in its adapter and rules doc.
  Report it; don't work around it in the layout.

## Pitfalls seen so far

- Anchors are the footprint's min corner **after** rotation, not the prefab's pivot.
- Rotation is relative to the prefab root's own rotation: rot 0 places it as authored (NK's
  Wall SF already runs along grid y at rot 0).
- A 1 × 1 card can still have a long axis: NK's jersey barrier is 0.57 × 2.02 m and runs along
  grid y at rot 0, so it shields its cell's ±x sides and looks like a post from the ±y sides.
  Read the long axis from `describe_level_cards` (`colliderBounds` size x and z at rot 0) or the
  `FIT_BOX` notes after apply, and turn the piece so its long side faces the threat. Cover in the
  rules is still per cell; this is how it looks, which the owner will notice.
- Diagonal steps can't pass a blocked corner (in games with that rule), so a diagonal line of
  blockers seals.
- Buildings with walls (cards with a `walls` outline) are entered through their door sides only;
  the cell outside the door must be open. Their frame cells are walkable; other pieces may stand
  inside.
- Buildings overhang their frame: porches, slabs and overgrowth reach into neighbouring cells
  (the card's `roofed` rows mark a porch; `footprintOffset` says the model sits off the frame's
  centre). Those cells stay walkable, but a piece placed there clips into the building.
- Road and sidewalk tiles often span several cells (NK's are 2 × 2); check their cells with
  `query_level_catalog` before tiling a run.
- **Roads have a direction and lanes.** Before laying a road, read the game's road-kit rules if it
  has them (NK: `level-rules.md` § *Road kit* and its recipes file). In Synty's kit a road tile is
  one lane running along its own +Z, so a two-lane road is two rows of tiles turned 180° apart, and
  sidewalk tiles face the road with their +Z. A single row of road tiles is half a road; copying a
  recipe is the safest way to get junctions and parking right.
- **Big layouts: build them in a script.** Hand-writing hundreds of tiles drifts. `scripts/layout_stamp.py`
  (beside this file) stamps recipe blocks or code-built tiles into a layout, turning a block by quarter
  turns with each tile's own rotation and anchor kept right, and refuses two ground tiles on one cell.
  Write the file into the project (`Assets/...layout.json`), then `refresh_assets` and `validate_layout(path=...)`
  instead of passing a large layout inline.
- A layout whose size differs from the level's board is refused by apply.
