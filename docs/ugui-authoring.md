# uGUI authoring

USB creates normal, editable Unity UI. There are no generated scripts, runtime
listeners, or USB components on the authored objects. Existing hierarchy,
component, field-editing and asset-search tools remain useful alongside these tools.

## Tools

| MCP tool | Purpose |
| --- | --- |
| `author_ui(operations, name="Author uGUI")` | Create, configure and wire a batch in one Undo group; never saves. |
| `inspect_ui(instance_id, camera_instance_id?, max_nodes=100, include_references=false)` | RectTransform subtree, components, projected bounds, visibility/interactivity, groups, layout drivers and persistent callbacks. |
| `raycast_ui(x, y, camera_instance_id?)` | Actual raycaster hits at primary-display pixels, origin bottom-left. |
| `undo_ui_batch(undo_token)` | Revert the untouched latest batch using its returned `undoToken`. Refuses intervening Undo activity; use Unity's Undo history if expired. |
| `save_ui_context(instance_id, path?)` | Explicitly save the **entire containing scene or current prefab contents**, including other pending edits. A new scene needs an unused `Assets/.../*.unity` path in an existing folder. |

The UPM package already depends on uGUI. For TMP on Unity 2022.3, install
`com.unity.textmeshpro`; Unity 6 includes TMP in uGUI. Import **Window > TextMeshPro >
Import TMP Essential Resources** and supply an explicit `TMP_FontAsset` reference.
Missing types, settings, fonts and unusable font materials produce actionable errors.
USB does not choose fonts, sprites, cameras or an input backend for you. Configure
an EventSystem and the project's input module for runtime button interaction.

## Batch operations

Each operation has `op` plus the fields below. Operations run in order. Unknown
properties are errors. Refer to an earlier `create` with `{"ref":"id"}`.

| `op` | Fields |
| --- | --- |
| `create` | `id`, `kind`, `name`, optional `parent`, `properties`, `rect`, `allowDriven` |
| `add_component` | `target` GameObject, `component`, optional `properties` |
| `configure` | `target` component, `properties` |
| `set_rect` | `target` GameObject, `rect`, optional `allowDriven` |
| `set_fields` | `target` component, `fields` mapping serialized paths to typed object references or reference arrays/lists |
| `button_on_click` | `target` Button, `listeners`, optional `mode`: `append` (default) or `replace` |

Create kinds: `Canvas`, `RectTransform`, `Panel`/`Image`, `Button`, `TextMeshProUGUI`.
Non-Canvas elements require a parent with RectTransform. Buttons include an Image;
create a separate TMP child when a caption is wanted. TMP labels default to
`raycastTarget:false`. Root objects use the UI layer; children inherit their parent.

Canvas creation requires explicit `properties.renderMode`: `ScreenSpaceOverlay`,
`ScreenSpaceCamera` or `WorldSpace`. The latter two also require `worldCamera`.
Canvas includes CanvasScaler (initially ConstantPixelSize) and GraphicRaycaster;
configure these components explicitly for other scaling/raycast policies.

### Properties

`properties` uses public Unity property names and named enums. Common choices:

| Component | Properties |
| --- | --- |
| Canvas | `renderMode`, `worldCamera`, `planeDistance`, `sortingOrder`, `overrideSorting`, `pixelPerfect`, `targetDisplay` |
| CanvasScaler | `uiScaleMode`, `scaleFactor`, `referenceResolution`, `screenMatchMode`, `matchWidthOrHeight`, pixel/unit/DPI settings |
| GraphicRaycaster | `ignoreReversedGraphics`, `blockingObjects`, `blockingMask` (integer layer mask) |
| Image/Panel | `color`, `sprite`, `type`, `preserveAspect`, fill settings, `raycastTarget` |
| Button | `interactable`, `navigation`, `transition`, `colors`, `targetGraphic` |
| TextMeshProUGUI | `text`, `font`, `fontSize`, `color`, `alignment`, `fontStyle`, `enableWordWrapping`, `overflowMode`, `raycastTarget` |
| Horizontal/VerticalLayoutGroup | `spacing`, `padding`, `childAlignment`, child control/expand/scale flags, `reverseArrangement` |
| GridLayoutGroup | `cellSize`, `spacing`, `padding`, `childAlignment`, `startCorner`, `startAxis`, `constraint`, `constraintCount` |
| LayoutElement | `ignoreLayout`, min/preferred/flexible width/height, `layoutPriority` |
| ContentSizeFitter | `horizontalFit`, `verticalFit` |
| CanvasGroup | `alpha`, `interactable`, `blocksRaycasts`, `ignoreParentGroups` |

All listed components support `enabled`. `add_component` supports the layout
components, CanvasGroup, CanvasScaler and GraphicRaycaster; use `configure` if
already present. It does not add arbitrary gameplay scripts.

Vectors use `{x,y}` or `{x,y,z}`, colors `{r,g,b,a}` (alpha defaults to 1), padding
`{left,right,top,bottom}`. `navigation` accepts partial `mode`, `wrapAround` and
`selectOnUp/Down/Left/Right` references. `colors` accepts partial ColorBlock values.
Omitted properties, including nested navigation/color-block values, stay unchanged.
Button image properties belong to its Image component, not its Button component.
An unsupported property error lists the available properties for that component.

### References and callbacks

References accept an integer instance ID, or exactly one of:

```json
{"instanceId": 12345}
{"globalObjectId": "GlobalObjectId_V1-..."}
{"path": "Assets/UI/MyFont.asset"}
{"guid": "asset-guid", "fileID": 11400000}
{"ref": "weapon", "component": "UnityEngine.UI.Button"}
```

Add `component` and optional zero-based `componentIndex` to select a component
on a GameObject. Use full type names if ambiguous. Asset `fileID` selects a
subasset explicitly; ambiguous assets are rejected. `null` clears a reference.
Targets cannot be null. Returned object/component instance IDs work with existing
USB tools. GlobalObjectIds become persistent **after saving**; inspect again after
saving to obtain valid IDs, and refresh instance IDs after reopening/reloading.

`set_fields` uses Inspector serialized field paths, including nested fields and
`.Array.data[index]`. Values must match the declared Object type; arrays/lists are
replaced only when explicitly supplied. Use existing `set_field_values` for other
serialized values; it shares the typed reference resolver. For native serialized
fields without managed type metadata, that existing tool retains Unity's native
assignment behavior.

A listener has `target`, `method`, optional `mode` (default `void`), `argument`,
`argumentType` (object mode only), and `state` (default `RuntimeOnly`). Supported
modes: `void`, `int`, `float`, `bool`, `string`, `object`. Methods must be public
instance methods returning void, with exactly the selected parameter type.
`argumentType` is a UnityEngine.Object-derived type, e.g. `UnityEngine.UI.Graphic`.
Listeners are ordinary Inspector-visible persistent UnityEvents. `replace` with
`listeners:[]` clears them; omitted listeners are never silently removed.

### Layout and transactions

`rect` supports `anchorMin`, `anchorMax`, `pivot`, `anchoredPosition`, `sizeDelta`,
`offsetMin`, `offsetMax`, `localEulerAngles`, `localScale`. Application order is
anchors → pivot → position/size **or** offsets → rotation → scale, regardless of
JSON key order. Mixing offsets with position/size in one operation is rejected.
Units are parent-local UI units, rotations are degrees. Offsets and size/position
are coupled Unity properties; changing one representation updates the other.

Active parent layout groups, fitters, Unity's recorded driver and root screen-space
Canvases are reported. Rect edits on a driven element are conservatively rejected,
even when a particular axis might be free. Prefer configuring its LayoutElement or
driver. `allowDriven:true` acknowledges that Unity may overwrite the value and
returns a warning. Conflicts introduced by earlier operations are checked again
at execution time and roll back the batch.

Batches accept 1–200 operations, validate references/types/properties/callbacks
before mutation where possible, then execute on the existing Editor main-thread
queue. An execution failure reverts the entire Undo group, including reference
assignments and layout changes to existing siblings. The scene can remain dirty;
rollback does not save. Exceptions from third-party editor callbacks with external
side effects cannot be made transactional.

A batch stays in one scene or current Prefab Stage. Scene prefab instances retain
their links and record overrides; USB does not unpack or apply overrides to source
assets. In Prefab Mode, turn **Auto Save off**, target the current prefab contents,
and explicitly save. Asset objects cannot be edited directly. Saving and Prefab
Stage edits reject Packages and recognized vendor folders (Synty, ThirdParty,
Third Party, 3rd Party, Vendor, Plugins), plus read-only assets. These folder checks
cannot recognize every vendor: use your own Assets folder for authored content.
Saving invalidates the batch Undo token. Authoring and saving require idle Edit Mode.

## Diagnosing input

`inspect_ui` forces calculated layout and reports parent-local rect values and
projected screen AABBs. Visibility is an estimate: hierarchy/Graphic/Canvas state,
alpha, camera culling mask, viewport, CanvasRenderer culling and CanvasGroups. It
is not a shader/depth-occlusion test. Supply the **actual gameplay viewing camera**
for world-space UI; the Canvas camera or a labeled Camera.main fallback is otherwise
used. Selectable interactability and raycast eligibility are separate values.

`raycast_ui` calls Unity raycasters, not rectangle intersection tests. With an
EventSystem, hits have EventSystem priority order. Without one (including typical
Edit Mode), loaded active scene raycasters are queried directly and ordering is
labeled per-raycaster. Each hit identifies its raycaster and event camera; the
optional viewing camera is used only for visibility diagnosis. Let the Game view
render after authoring so native UI geometry/depth is current.

A GraphicRaycaster can hit world-space UI whose layer is excluded by the viewing
camera. Such hits get an explicit warning. Zero alpha or a non-interactable Button
also does not necessarily stop hits. Set `raycastTarget:false` on decorative
Graphics, disable the relevant GraphicRaycaster, or set `CanvasGroup.blocksRaycasts`
to false as appropriate. A hit only blocks gameplay if the game's input policy
uses that hit; an overlapping rectangle alone proves neither a hit nor blocking.

## Acceptance example

[Example MCP requests](examples/ugui-acceptance.json) create a panel, three movement
buttons, a weapon button and a TMP status label, then wire an **existing** gameplay
component and `SelectWeapon(int)` callback. Replace example component ID `12345`
with the component's ID from inspection, and adapt field/method names to your game.
The explicit font path must exist; use `find_unity_files` if needed.
Replace illustrative HUD ID `23456` in the follow-up requests with the returned
ID, and derive raycast coordinates from the inspected bounds for your Game view.

1. Call `author_ui` with the example's `author` arguments.
2. Use returned `objects.hud.instanceId` for `inspect_ui`; set `include_references:true`
   when checking wiring. Use a Button's reported screen-bounds center for `raycast_ui`.
3. Call `undo_ui_batch` with the returned token before any other authored edits.
4. Repeat the author request. Call `save_ui_context` with the new HUD ID; for a new
   scene use an unused path in an existing project-owned folder.
5. Reopen the scene in Unity, reacquire IDs with `get_scene_hierarchy`, and inspect
   references and `buttonOnClick`. Reopening is ordinary Editor scene management,
   not an operation in this focused UI API. The integration fixture automates it.

See [verification and remaining gaps](ugui-verification.md).
