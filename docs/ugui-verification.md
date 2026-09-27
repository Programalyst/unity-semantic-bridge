# uGUI verification

Verified on macOS with Unity **2022.3.62f3** and **6000.3.24f1** in disposable
projects, using Metal rendering and TMP Essential Resources. No consuming project
was changed. Unity 2022.3 resolved uGUI 1.0.0 / TMP 3.0.7; Unity 6 resolved built-in
uGUI 2.0.0 with TMP.

## Results

- **13/13 UI integration tests pass on each version.** Requests enter through
  `McpMessageHandler`, the same dispatcher used by the Editor RPC bridge.
- **10/10 Python tests pass**, including schema registration and routing/defaults
  for all five new MCP tools. Python transport is mocked in these tests.
- The wider Editor suite run passed 35 of 40 cases on each version: two existing
  refresh tests failed, two mesh tests were inconclusive without a Mesh fixture,
  and the optional Synty FBX test was skipped. This run preceded the final two
  UI test additions; the final 13-case UI suite was run separately on both versions.
- Both refresh failures (`RealNoOpRefreshSettles`,
  `RealNonCodeRefreshImportsTextAndSettles`) also reproduce on pre-feature commit
  `af28fd0` in a separate Unity 2022.3 project: 6/8 refresh tests pass, and those two
  exceed their 1,200-tick settling limit. This change does not modify refresh code.

The acceptance fixture creates an overlay Canvas/panel, three movement buttons,
a weapon button and explicit-font TMP label. It assigns typed reference arrays to
an existing test gameplay component, adds a persistent callback, inspects layout,
queries a real GraphicRaycaster hit, calls `undo_ui_batch`, recreates and explicitly
saves. The fixture then reopens the scene and checks references, callback arguments
and the saved GlobalObjectId. Only scene reopening and fixture setup/teardown use
Editor lifecycle APIs outside tool requests; no temporary authoring scripts are
created or executed by the tools.

Additional checks cover:

- Missing/stale references, wrong types, invalid method signatures, missing fonts,
  null font edits, and temporarily unavailable TMP Essential Resources.
- Every supported persistent callback argument mode; callbacks invoke successfully
  with `EditorAndRuntime` state in the fixture. Default RuntimeOnly state is checked
  through serialization and persistence, not a simulated gameplay click.
- Layout groups, Grid, LayoutElement, ContentSizeFitter, deterministic anchor/offset
  ordering, driven-edit rejection and explicit `allowDriven` warnings.
- Failure after creating objects and changing existing fields: the entire batch
  rolls back. Intervening unrelated Undo edits are protected.
- Partial navigation/ColorBlock edits preserve omitted values. Existing
  `set_field_values` still supports primitives, native Camera references, and
  shares typed reference checks for managed fields.
- Scene prefab-instance overrides and child additions preserve source assets and
  Undo cleanly. Prefab Mode rejects Auto Save, remains unsaved until requested,
  saves cleanly, and preserves edits after reopening.
- Package/vendor/traversal save paths are rejected.
- A world-space Graphic receives an actual raycast while its viewing camera's
  culling mask hides it. The report identifies the mismatch; setting
  `CanvasGroup.blocksRaycasts=false` removes that hit.

## Reproducing

Use a **disposable project**. The integration tests replace scenes and temporarily
move the fixture's TMP Settings asset; they deliberately skip in an interactive
Editor. Install USB, include its package name in `Packages/manifest.json`'s
`testables`, install Unity Test Framework, and import TMP Essential Resources.
The font fixture expects
`Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset`.

```sh
"/path/to/Unity" -batchmode -projectPath /path/to/disposable-project \
  -runTests -testPlatform EditMode \
  -testFilter Gamenami.UnitySemanticBridge.Editor.Tests.UiAuthoringTests \
  -testResults /tmp/usb-ui-results.xml -logFile /tmp/usb-ui-tests.log
```

Keep graphics enabled: the tests open Game view and wait for rendered UI geometry
before checking real raycasts. Do not use `-nographics` for this suite.

From `mcp-editor-bridge/`:

```sh
uv run python -m unittest discover -s tests -p 'test_*.py'
```

## Remaining gaps

- Intermediate supported Unity versions, Windows/Linux, and other render pipelines
  were not tested. The tested endpoint versions compile without a hard TMP assembly
  dependency. Complete absence of TMP's package was not separately tested; missing
  resources/font paths were. uGUI itself is a required UPM dependency.
- No end-to-end MCP-client/HTTP run or BattleStations Next gameplay smoke test was
  performed. Dispatcher behavior and Python mapping are tested separately; the
  existing main-thread transport is reused unchanged.
- Actual runtime input-module behavior, pointer/device differences, multi-display,
  render-texture/XR UI, complex masks/occlusion, multiple viewing cameras, nested
  prefab variants, and VCS checkout failures were not exercised. Visibility is an
  estimate; raycast hits are reported separately from gameplay input consumption.
- Asset persistence was checked by reopening scenes/prefabs in the same Editor
  process, not by restarting Unity or building a player.
- Third-party editor callbacks can have non-Undoable side effects. Batch rollback
  covers USB's Unity object edits, not external file writes performed by other code.
