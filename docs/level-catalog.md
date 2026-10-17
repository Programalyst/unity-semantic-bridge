# Level catalog: cards for level pieces

The level catalog holds one **card** per level-relevant prefab, so an agent can pick
pieces and size them on the grid without opening each prefab. It is a separate file from
the asset catalog (`asset-catalog.json`, which serves character outfits), so neither file
grows the other. Cards feed [grid layouts](grid-layouts.md): since Phase 4c, apply and bake
read each reviewed piece's `placementScale`, `lift`, footprint and `cover` from its card,
which they build on demand from the prefab and the overrides file (`LevelCardSource`), so a
game project needs only the overrides file, not a generated catalog. Background:
[asset intelligence plan](asset-intelligence-plan.md) §3 *Catalog cards* and *Piece scale*.

- Output: `<Project>/AssetCatalog/level-catalog.json` (cards keyed by prefab GUID) and
  thumbnails in `<Project>/AssetCatalog/level-thumbs/`.
- Overrides: `<Project>/AssetCatalog/LevelCatalogOverrides.json` (optional, written by hand
  or by the review step).
- Code: `Editor/LevelCatalogGenerator.cs` (measuring, file output), run by
  `Editor/AssetCatalogRunner.cs`. The card model, footprints, role rules and the
  overrides parser are pure C# in `Runtime/Layout/LevelCard.cs` and
  `Runtime/Layout/LevelCatalogOverrides.cs` (no `UnityEngine`), so games and tests can
  use them.

## Running the generator

Usually in Asset Preview (the project with the full packs):

```text
generate_asset_catalog(catalog="level", scan_roots=["Assets/Synty/PolygonApocalypseWasteland"])
get_asset_catalog_status()   # poll until state is "done"
```

- `catalog="level"` scans `.prefab` files only (scan roots are searched recursively;
  default `Assets/Synty`). It is frame-pumped like the asset catalog, about 10 prefabs a
  frame, with a progress bar and cancel. When done, the status gives `itemCount`,
  `thumbnailCount` and per-role counts (`roles`).
- Each prefab is opened with `PrefabUtility.LoadPrefabContents`, measured and unloaded.
  Open scenes are never touched.
- Re-runs upsert cards by GUID, so you can scan one folder at a time. A prefab an
  override excludes is removed. Thumbnails already in `level-thumbs/` are reused, and
  ones the asset catalog captured in `thumbs/` are copied. Thumbnails no card uses are
  deleted.
- A bad overrides file stops the run with its error, so unreviewed roles and scales are
  never published silently. A prefab that fails to load is skipped with a Console warning.
- Spot check without writing anything: `describe_level_cards(paths=[...], cell_size=2)`
  returns each prefab's card plus its computed footprint (or why it isn't a level piece).
- Left out: prefabs with a `SkinnedMeshRenderer` (characters, creatures), with no
  visible mesh renderer (FX, empty helpers), and those the asset catalog files as hair,
  facegear, outfit or weapon. An override `role` brings any of them back.

The sample run of 2026-10-16 covered Wasteland `Prefabs/Props` and Apocalypse
`Prefabs/Buildings`: 983 cards with 983 thumbnails, in about a minute. The roles were 13
blocker, 85 building, 181 cover_full, 139 cover_half, 359 decor, 152 ground and 54 unknown.

## Card format

```json
{"levelCatalogVersion": 1, "generatedAt": "…", "unityVersion": "2022.3.62f3",
 "packs": ["PolygonApocalypse", "PolygonApocalypseWasteland"],
 "roles": {"blocker": 13, "cover_half": 139, "…": 0},
 "cards": {
  "6170827b052d9ec4bb43fcf8472fc157": {
   "id": "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_01.prefab",
   "name": "SM_Prop_Barrier_01", "pack": "PolygonApocalypseWasteland",
   "levelRole": "blocker", "roleSource": "name", "cover": null,
   "visualBounds":   {"center": [0, 0.732, 0],      "size": [3.263, 1.446, 0.79]},
   "colliderBounds": {"center": [0, 0.747, -0.007], "size": [3.314, 1.476, 0.829]},
   "pivotOffset": [0, 0.009, -0.007], "height": 1.485, "lift": 0, "storeys": null, "skewed": false,
   "placementScale": [1, 1, 1], "scaleSource": "prefab", "rootRotation": [0, 0, 0],
   "thumbnail": "level-thumbs/PolygonApocalypseWasteland__Prefabs__Props__SM_Prop_Barrier_01.prefab.png"}}}
```

The key is the prefab GUID, which stays the same when pieces move to the game by a
GUID-preserving `.unitypackage`, so layouts written against Asset Preview's catalog
resolve in the game.

| Field | Meaning |
|---|---|
| `id`, `pack` | Path with leading `Assets/`, `3rd Party/` and `Synty/` dropped; the first folder left is the pack. The same prefab gets the same id in Asset Preview (`Assets/Synty/…`) and in a game that imported it under `Assets/3rd Party/…`. |
| `visualBounds` | Box around every enabled mesh renderer on an active object, in **root space at root scale 1**: the root is reset to the origin, unrotated and at scale 1 before measuring, and child transforms count. Meters. |
| `colliderBounds` | The same for enabled, non-trigger colliders (box, sphere and capsule shapes; a mesh collider's mesh bounds). `null` when the prefab has none. |
| `pivotOffset` | Bottom centre of the footprint bounds relative to the pivot (root space, scale 1). Footprint bounds are the colliders for blocking roles when present, else the visual bounds. |
| `height` | Top of the footprint bounds above the floor, at `placementScale` and `rootRotation`, in meters: the top above the pivot plus `lift`. It drives cover. |
| `footprintOverride` | `[x, z]` meters the piece claims on the grid at layout rotation 0, instead of its bounds; `null` unless an override sets it. For pieces allowed to overhang their cells visually (e.g. a tyre heap scaled past one cell). |
| `lift` | Height of the pivot above the floor when placed, in meters (0 unless an override sets it). For pieces modelled below their pivot, such as the Wasteland barricades pivoted at mid-height, which otherwise sink half into the floor. Layouts place the pivot at floor + `lift`. |
| `storeys` | Buildings only: `height / 3 m`, rounded, at least 1; an override can set it. |
| `skewed` | A measured child is rotated off 90° steps (by more than about 0.8°), so the boxes overstate the size. |
| `levelRole` | `blocker`, `cover_half`, `cover_full`, `ground`, `decor`, `building` or `unknown` (see below). |
| `roleSource` | `override`, `name` or `geometry`: which rule decided the role. |
| `cover` | `None`, `Half` or `Full` from the role (or an override); `null` for `blocker` and `unknown`, where the game's adapter decides (NK: below 1.2 m Half, otherwise Full). |
| `placementScale` | The root scale layouts place the piece at. Until reviewed it is the prefab root's own scale (`scaleSource: "prefab"`); the overrides file sets the approved one (`"override"`). |
| `rootRotation` | The prefab root's own local rotation (Unity Euler degrees). Layout rotations are quarter turns **on top of it**, so rot 0 places the prefab as authored. |
| `thumbnail` | PNG path relative to `AssetCatalog/`. |

## Footprints are computed on request

Footprint cells and fit error are not stored, so one card serves any cell size and any
scale. `LevelCards.Footprint(card, cellSize, scale?)` (`Runtime/Layout/LevelCard.cs`):

1. takes the footprint bounds (colliders for blocking roles when present, else visual),
   unless the card has a `footprintOverride`, whose size gives the cells instead (step 4; `Overridden` is set
   and the fit error is negative where the piece overhangs),
2. scales them by `placementScale`, or by the `scale` argument for a per-piece exception,
3. turns them by `rootRotation` and takes the axis-aligned box,
4. rounds each horizontal side up to whole cells after a 5 cm tolerance (the same rule
   as the applier), with at least one cell.

The result (`CardFootprint`) holds, at layout rotation 0:
- `Width` (cells along grid x) and `Depth` (cells along grid y), with `ToSpec(cover)`
  giving the layout's `PieceSpec` and `RotatedSize(rot)` the board size after quarter turns;
- `Size` in meters (X along grid x, Y up, Z along grid y);
- `Center`, the bounds centre relative to the pivot, which apply subtracts to centre the
  piece on its cells (X/Z; the pivot goes to floor + `lift`);
- `FitErrorX`/`FitErrorZ`, cells minus meters: the slack left in the footprint. It is
  slightly negative when the piece is longer than its cells but within the tolerance;
- `Height`, above the floor (the card's `lift` included);
- `Approximate`, set when the root rotation is not a quarter turn about Y or the card is
  skewed, because the box then overstates the size.

Checked against `nuclear-knights/docs/level-rules.md`:

| Piece | Card (scale 1) | At `placementScale` | Footprint (2 m cells) |
|---|---|---|---|
| NK `Wall SF` (root turned 90°, scale 1.65, 2, 1.5) | collider 2.37 × 1.45 × 0.50 | 3.91 × 2.90 × 0.75 m, as hand-measured | 1 × 2 at rot 0 (2 cells along grid y), fit 1.25 / 0.09 m |
| NK `Wall SF (half)` (scale 1.65, 0.8, 1) | collider 2.30 × 1.40 × 0.70 | 3.80 × 1.12 × 0.70 m | 1 × 2, fit 1.30 / 0.21 m |
| Wasteland `SM_Prop_Barrier_01` (scale 1) | visual 3.26 × 1.45 × 0.79 (hand: 3.26 × 1.41–1.46 × 0.79) | collider 3.31 × 1.48 × 0.83 m | 2 × 1, fit 0.69 / 1.17 m (the "about 0.7 m" slack) |

The Wall SF rows come from `describe_level_cards` in NK; the barrier row comes from the
Asset Preview sample. Barrier_03/04 also match their hand-measured visual sizes.

## Role detection

The first rule that decides wins:

1. **Overrides file** (`role`, below).
2. **Name, then folder** (case-insensitive substrings of the file name, then of the
   folder path), in this order:
   - `Greeble`, `Decal`, `Sign` → `decor` (so `Greeble_Wall` and `Sign_Road` aren't walls or roads)
   - `Road`, `Sidewalk` → `ground`
   - `Sandbag`, `Barrier`, `Wall`, `Fence` → cover by height: below 1.2 m `cover_half`,
     2 m or more `cover_full`, in between `blocker` (it blocks; its cover class needs review)
   - `Bld_` → `building` when the box is at least 2 × 2 m and 2 m tall; smaller `Bld_`
     parts (doors, signs, stools) fall through to geometry
3. **Geometry** (footprint bounds at `placementScale`): longest side below 0.5 m →
   `decor`; height below 0.35 m → `ground` (floor tiles, walkways); below 1.2 m →
   `cover_half`; 2 m or more → `cover_full`; otherwise `unknown`.
4. Vision on the thumbnail for leftover `unknown` cards is not built yet.

Thresholds are constants on `LevelCards`. Expect mistakes from geometry: doors come out
as `cover_full`, and beams and ceiling tiles are guessed from their size. The 4b review
fixes them through the overrides file.

## Overrides file

`AssetCatalog/LevelCatalogOverrides.json` is parsed strictly: unknown keys, unknown
roles or covers, non-positive scales and bad JSON are errors that name the entry.

```json
{
  "v": 1,
  "rules": [
    {"match": "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_*", "role": "cover_half", "placementScale": [1, 0.8, 1]},
    {"match": "**/SM_Bld_Door_*", "role": "decor"}
  ],
  "pieces": {
    "6170827b052d9ec4bb43fcf8472fc157": {"role": "cover_full", "placementScale": [1.2, 1.4, 1.2], "note": "approved 2026-10-20"},
    "PolygonApocalypse/Prefabs/Buildings/SM_Bld_Motel_01.prefab": {"storeys": 2},
    "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Throne_01.prefab": {"exclude": true}
  }
}
```

- `rules`: family defaults. `match` is a glob over the card `id`: `*` stays within one
  folder, `**` crosses folders, `?` is one character. Every matching rule applies in file
  order, and a later rule overrides an earlier one field by field.
- `pieces`: **per-piece exceptions**, keyed by prefab GUID (preferred: it survives the
  export to the game) or by card `id`. They win over the rules, and a GUID entry wins
  over an id entry.
- Entry fields, each optional:
  - `role` sets `levelRole` (`roleSource: "override"`).
  - `cover` (`None`, `Half`, `Full`) replaces the cover the role implies.
  - `placementScale` is `[x, y, z]` or one number for a uniform scale (`scaleSource: "override"`).
  - `storeys`
  - `footprint` (`[x, z]` meters at layout rotation 0, both positive) is the size the piece claims on the
    grid; its cells come from this instead of the bounds, so a piece can overhang its cells visually. The
    card stores it as `footprintOverride`. Apply fits the blocking collider to the claimed cells: colliders
    that reach past them are disabled and replaced by one box inset 0.1 m ([grid layouts](grid-layouts.md)).
  - `lift` (meters, may be negative) raises the pivot above the floor; it is added to `height`, so it changes
    the detected role and cover too.
  - `exclude: true` leaves the prefab out of the catalog.
  - `note` is free text for review history.
- Height, cover and storeys are measured at the overridden scale. Edit the file, then
  re-run the generator on the affected folders.

A per-piece scale inside one layout (the optional sixth piece field, plan §3) is a
different thing. The overrides file sets the scale every layout uses, and
`LevelCards.Footprint`'s `scale` argument covers the in-layout exception.

## Tests

- `Tests/Editor/LevelCardTests.cs` (pure): Wall SF, Wall SF (half) and Barrier_01
  footprints against the hand measurements, cell size and scale exceptions, the 5 cm
  tolerance, collider-versus-visual bounds, the Euler convention, approximate
  footprints, role rules, overrides (rules, id and GUID entries, exclude, strict
  errors) and the card JSON round trip.
- `Tests/Editor/LevelCatalogGeneratorTests.cs` (Editor): real prefabs in a scratch folder
  measured through `LoadPrefabContents` (root scale and rotation ignored, child scales
  counted, skew detection, characters and empty prefabs left out, overrides by GUID,
  `level-catalog.json` keyed by GUID with exclusion), ids independent of the scan root,
  and the Euler math against `Quaternion.Euler`.
- Run: `bash scripts/test-unity.sh -testFilter "Gamenami.UnitySemanticBridge.Editor.Tests.LevelCardTests|Gamenami.UnitySemanticBridge.Editor.Tests.LevelCatalogGeneratorTests"`.
- `mcp-editor-bridge/tests/test_level_catalog_tools.py`: the `catalog` parameter and
  `describe_level_cards` registration.

## Cards in apply and bake

A game project (NK) keeps no `level-catalog.json`: the generated files under `AssetCatalog/` are gitignored and only the overrides file is committed. The full catalog lives where the packs are scanned (Asset Preview); NK's `level-rules.md` (*Choosing pieces for a layout*) says where. A query tool is Phase 5.

- Which pieces have a card: those with a **per-piece entry** (GUID or id) in the project's
  `LevelCatalogOverrides.json`, i.e. reviewed pieces; family rules alone don't give one.
  `LevelCatalogOverrides.HasPieceEntry` decides; `LevelCardSource.ForProject()` builds the
  card with `LevelCatalogGenerator.BuildCard`.
- Apply instantiates at `placementScale`, places the pivot at the floor + `lift` (+ the game
  adapter's `GroundLift` for role `ground`), sizes the piece by `LevelCards.Footprint(card,
  cellSize, blocking)` (collider bounds when placed as a blocker, visual bounds as decor) and
  passes `cover` to the game's piece hook (`None` for movement-only blockers; the adapter
  decides only when the card's cover is null).
- Bake reads card pieces at their card footprint and reports any instance whose scale differs
  from its card's `placementScale` (`OFFGRID_SCALE`).

## Not yet

- The scale and plausibility validators are 4d. The 4b scan, review and import are done:
  NK `docs/level-piece-review.md` and `docs/level-rules.md` (§ *Level pieces and the catalog overrides*).
- No thumbnail vision for `unknown` cards, and no `query_level_catalog` tool (Phase 5).
