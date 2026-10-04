# Bounded semantic batches and prefab inspection

Three additive tools cover bulk Editor inspection/edits and prefab comparison.
Existing individual tools retain their contracts. Restart/reconnect the Python
MCP server after updating, and let Unity finish compiling the package.

## The gap this solves

Previously, comparing the joint axes/anchors in two prefab assets required
finding Editor objects and inspecting each component separately, or writing a
custom file parser. Batching known objects alone does not discover components
inside asset paths, and text inspectors format vectors for display.

`inspect_prefab_assets` accepts explicit asset paths, traverses their resolved
imported hierarchies, filters by component type, and returns selected serialized
properties as typed JSON. It preserves the precision of Unity's imported numeric
values, including tiny nonzero vector components. It does not preserve the
original YAML spelling or promise source-file values identical to Unity's imported
representation. It needs neither scene instances nor a regex YAML parser.

## Prefab query

```python
inspect_prefab_assets(
    paths=[
        "Assets/Prefabs/Characters/Base/BR Base.prefab",
        "Assets/Prefabs/Characters/Wasteland/Big Mutant_No_Face_01.prefab",
    ],
    component="UnityEngine.CharacterJoint",
    properties=["m_Axis", "m_SwingAxis", "m_ConnectedAnchor"],
)
```

Paths are relative to the connected Unity project, beginning with `Assets/` or
`Packages/`, ending with `.prefab`, without traversal segments or backslashes.
Only already imported prefab assets are read. Variants, nested prefabs, inactive
objects, repeated names and multiple components are supported. A component index
is relative to the matching type on its GameObject; hierarchy paths include sibling
indices. Prefer fully qualified component names to avoid ambiguous short names.

Each ordered result has `id` equal to the requested path, `assetPath`,
`source: "resolvedImportedPrefabAsset"`, and `objects`. Each item contains
GameObject/component identity, hierarchy path, component index, and properties:

```json
{
  "m_Axis": {
    "type": "Vector3",
    "value": {"x": 0.4568338096141815, "y": -2.948218e-7, "z": 9.464566e-8}
  }
}
```

Values are illustrative. Numbers come from serialized numeric accessors rather
than `Vector3.ToString()`. Asset identities contain GlobalObjectId and available
GUID/fileID information but no instance IDs or reusable mutation tokens. The
query root asset path plus sibling-indexed hierarchy path and matching component
index distinguish an occurrence of a nested prefab from its source asset identity.
These are imported asset values, excluding unsaved Prefab Stage/scene overrides.

Inspection loads imported assets through AssetDatabase; it does not instantiate,
open preview scenes/Prefab Mode, invoke a save/reimport, change selection, register
Undo or dirty scenes/assets. No temporary instances require cleanup.

## Editor references and batches

Acquire references using IDs returned by existing hierarchy tools:

```python
acquire_editor_references(instance_ids=[12345, 67890])
```

Use the returned `results[i].object.reference` opaque strings as `target` values.
Verify the returned identities before editing: initial acquisition resolves IDs
as they currently exist and cannot prove an ID obtained earlier is still valid.
Subsequent tokens retain their exact Unity object and original scene, and fail on
deletion, scene migration/unload, domain reload, scene open/close/create/active-scene
change, Prefab Stage changes or Play Mode changes. The cache holds at most 2048
references; oldest references are evicted, never rebound. Read operations can
return new tokens for discovered components on explicitly identified objects.

```python
semantic_batch(operations=[
    {"id": "right", "op": "inspect", "target": "TOKEN_A",
     "component": "UnityEngine.CharacterJoint", "componentIndex": 0,
     "properties": ["m_Axis", "m_ConnectedAnchor"]},
    {"id": "left", "op": "inspect", "target": "TOKEN_B",
     "component": "UnityEngine.CharacterJoint",
     "properties": ["m_Axis", "m_ConnectedAnchor"]},
])
```

A GameObject target without a component selector lists component identities and
tokens. Selected-property inspection needs a component reference or a GameObject
plus component selector. A direct component token must omit the selectors.
Live inspection results identify their source as `liveEditor`.

Supported reads include primitives, enum indices, vectors, integer vectors,
quaternions, colors, object identities, Rect/Bounds, arrays and serialized generic
structs. Unsupported property types and missing properties return errors rather
than placeholder values. Selected property paths are required for component and
prefab inspection; there is no unbounded dump of all fields.

## Property edits and dry runs

```python
semantic_batch(
    name="Adjust joint axes",
    dry_run=True,
    operations=[
        {"id": "axis", "op": "set_fields", "target": "JOINT_COMPONENT_TOKEN",
         "fields": {"m_Axis": {"x": 0, "y": 0, "z": 1}},
         "expected": {"m_Axis": {"x": 1, "y": 0, "z": 0}}},
    ],
)
```

Run with `dry_run=False` to apply the reviewed request. A dry run is validation,
not an approval token or reservation: execution always validates live state again.
Object values accept `null`, `{"reference": "OPAQUE_TOKEN"}`, or the existing
explicit asset-reference syntax (`path`/`guid`, optional `fileID`, component/index).
Raw instance IDs are rejected in mutation values. Object-reference types and
same-scene/Prefab Stage restrictions are checked, including native pointer types.

Edits use the existing `ComponentFunctions.ApplyPropertyValue` writer. Strict
validation rejects coercions, unknown enum names/indices, incomplete vectors,
nonfinite/out-of-range numbers, unsupported types, overlapping property paths,
structural hierarchy properties and serialization ownership/prefab metadata.
Existing supported arrays/generic structs can be edited within collection limits.
Integer edits retain the existing setter's 32-bit range; float edits retain its
float range. Inspection can return wider serialized numeric values.

Mutation targets must be editable loaded scene/Prefab Stage components, not asset
objects. Reuse the existing editability policy: one scene/stage, immutable prefabs
rejected, current Prefab Stage only while Prefab Mode is open, Auto Save off, and
project-owned editable stage paths. These constraints deliberately defer editing
prefab assets by path. No save is implicit.

Preconditions compare raw structured values recursively and exactly, without an
epsilon. JSON integer/float spellings with equal numeric values compare equally;
integer-to-integer comparisons retain integer precision. Enums compare by index.
For floats, reuse returned inspection values when exact equality matters. Object
preconditions compare the returned descriptive identity object or null. Partial
object/struct matches are not supported. All preconditions validate against the
initial live state during preflight and against live state again immediately before
each operation; requests that expect an earlier operation's future value during
preflight should be split into separate batches.

## Failure and execution semantics

Only `inspect` and `set_fields` operations are allowlisted. IDs are unique and
results preserve input order. A batch is entirely read operations or entirely
mutation operations; mixing them is rejected before work. Destruction, creation,
reparenting, arbitrary C#, builds, deletion and Play Mode transitions are excluded.

All entry points run synchronously on the existing main-thread request queue in
idle Edit Mode. No cross-tick execution or snapshot-consistency claim is made.
Unity callbacks may run during native calls; later mutation operations revalidate.

Reads continue after errors. Mutations preflight every operation before opening
an Undo group. If any validation fails or reaches a limit, none are applied:
invalid operations have `error`/`truncated` status, and other operations are
`skipped` with `batchValidationFailed`. Successful dry runs return `validated` and
zero applied operations, discarding uncommitted SerializedObject buffers.

Execution uses one named Undo group and the existing dirty-state/prefab-override
helper. This is **not transactional atomicity**. On the first execution failure,
remaining operations are `skipped`; prior `applied` operations remain changed and
can be recovered with Unity Undo. `unchanged` indicates no serialized change.
`partialApplication`, `appliedOperations` and the Undo group number are reported.

Each applied result reports the submitted property's `before` and normalized
`after` values with `valuesReported: "submittedSerializedValues"`. Project
`OnValidate`/other callbacks can alter those values or other objects. Their effects
are outside this submitted-write report; inspect again to observe final values.
If applying throws after application begins, the operation is
`applicationUncertain`, its submitted-write report is retained, and later
operations stop. Inspect affected objects/Undo history before further action.
Neither dry runs nor Undo groups guarantee arbitrary project callbacks are atomic.

The existing request log records the batch once; execution adds one outcome summary
through BridgeRelay. Existing Unity object-change event hooks remain active.
No per-property duplicate logs, automatic saves or mutation retries are added.
Mutation batches explicitly disable even reload-grace connection retries. Existing
individual tools and read requests retain their connection-establishment behavior.
Responses, application errors and timeouts are never replayed by these new tools.

## Bounds

| Limit | Default | Maximum |
| --- | ---: | ---: |
| Operations / acquired IDs | 64 hard cap | 64 |
| Prefab paths | 32 hard cap | 32 |
| Property paths / edit fields / expected paths per operation | 32 hard cap | 32 |
| Hierarchy nodes, shared across prefab paths | 512 | 4096 |
| Discovered component targets | 128 | 512 |
| Hierarchy/property depth | 32 | 64 |
| Elements/struct children per collection | 32 | 128 |
| Expanded property values, shared across validation and execution | 4096 | 16384 |
| UTF-8 response bytes | 262144 | 1048576 |
| Cooperative execution milliseconds | 100 | 1000 |

Optional `limits` uses `maxNodes`, `maxTargets`, `maxDepth`, `maxCollection`,
`maxValues`, `maxResponseBytes` and `maxMilliseconds`. Response limits have an
8192-byte minimum. Requests are capped at 262144 UTF-8 bytes; property strings at
4096 characters; operation IDs at 64 characters; property/asset paths at 256.

Time checks run between bounded units; Unity asset loads, type resolution, native
serialization, Undo and callbacks cannot be preempted. The elapsed time may exceed
the requested cooperative budget inside such calls; this is not a hard deadline.
Prefab traversal keeps a depth-bounded frontier, including on wide hierarchies.
The expanded-value budget includes repeated mutation validation, not just output.

`truncated`, `limitsReached`, per-result status/reason and detail-omission flags
make incomplete results explicit. Prefab queries retain already returned matches;
a component's incomplete property payload is omitted. Error-heavy responses can
omit error details to retain ordered operation statuses. Too-small budgets for
minimum statuses are rejected before execution. Mutation report space is checked
before applying writes; applied/uncertain write reports are retained.

## Validation and compatibility

Python registration, typed schema, transport and no-replay tests:

```sh
cd mcp-editor-bridge
.venv/bin/python -m unittest discover -s tests -v
```

Unity EditMode fixture: `SemanticBatchTests`. Run with the package in `testables`
in a disposable project; fixtures create/remove scratch prefabs, variants and
scenes. Tests cover read-only state, precise vectors, nested/variant resolution,
multiple components/repeated names, missing/stale targets, mixed read success,
strict values/object-reference types, limits, dry runs, partial application,
expected values, Undo/Redo and prefab overrides. Deterministic changes between
preflight and later operations exercise execution-time precondition revalidation;
separate human changes before a request also reject stale expected values.

Verified on macOS / Unity 2022.3.62f3 in a disposable project:

- All 14 new batch EditMode tests passed.
- The combined batch/UI/model run passed 36 tests with zero failures; the optional
  Synty model fixture was skipped because its asset was absent.
- All 15 Python tests passed, including existing tool contracts and a transport
  test proving mutation connection failures are not retried during reload grace.
- The maintained TestProject runner then passed 49 tests with zero failures and
  seven skips: four explicit preference tests, two explicit refresh tests and the
  optional absent Synty fixture. Fixture preparation and the full runner were
  exercised end to end.
- `git diff HEAD --check` passed.

Measured with two synthetic prefabs containing ten CharacterJoints each, and ten
separate objects receiving property edits:

| Task | Individual dispatcher requests | Batch dispatcher requests | Local individual time | Local batch time |
| --- | ---: | ---: | ---: | ---: |
| Compare 20 joints across two prefab assets | 20 | 1 | 4 ms | 18 ms |
| Edit scale on ten separate transforms | 10 | 1 with held references; 2 including acquisition | below 1 ms | 5 ms |

These are single-run in-process measurements, not an end-to-end HTTP/MCP
benchmark. Existing inspections return broader formatted text; the new query
also traverses assets and produces structured identities/values. Extra local
validation work can cost more than individual handlers while saving 95% of
inspection requests and 90% of edit requests with held references (80% including
one cold acquisition). A latency crossover or total wall-clock speedup has not
been measured. Initial object discovery for the individual inspector was excluded.

Use the maintained [TestProject](../TestProject/README.md) and its runner for
reproduction. Windows/Linux, newer Unity versions and a live MCP-client session
were not tested in this change. No game project, package publishing, commit or
push is required.

The implementation follows Unity's documented [SerializedObject behavior](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SerializedObject.html),
[imported asset loading](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.LoadAssetAtPath.html)
and [prefab override recording](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/PrefabUtility.RecordPrefabInstancePropertyModifications.html).
