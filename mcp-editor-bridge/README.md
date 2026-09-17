Bridge uses JSON-RPC 2.0 over HTTP.

- Python -> Unity: `POST http://127.0.0.1:1073/rpc` with `{"jsonrpc":"2.0","id":"...","method":"...","params":{...}}` -> `{"jsonrpc":"2.0","id":"...","result":"..."}`.
- Unity -> Python events: `POST http://127.0.0.1:1074/rpc` with JSON-RPC notifications/requests (e.g. `{"jsonrpc":"2.0","method":"unity/hierarchyChanged","params":{...}}`). Handled by `event_server.py` (health: `GET /health` on both ends).

## Refreshing external file edits

`refresh_assets() -> str` takes **no arguments**. Use it after another coding
session finishes writing files on disk. It queues `AssetDatabase.Refresh()` on
the Unity main thread through the existing JSON-RPC dispatcher. Unity chooses
what to import and whether compilation/domain reload is needed. It does not
rewrite scripts, force compilation/reload, change Play Mode, or save scenes.
Import itself can take time; the bridge never waits on the Editor thread for
compilation to finish. This follows Unity's [asset refresh lifecycle](https://docs.unity3d.com/Manual/AssetDatabaseRefreshing.html).

Example request to `POST http://127.0.0.1:1073/rpc`:

```json
{"jsonrpc":"2.0","id":"refresh-1","method":"refresh_assets","params":{}}
```

Example response (the token is generated per accepted request):

```json
{"jsonrpc":"2.0","id":"refresh-1","result":"Refresh queued (token=01234567-89ab-cdef-0123-456789abcdef). RELOAD_IMMINENT: Unity may compile/reload if needed. Call get_compilation_status to get the result."}
```

The response acknowledges scheduling, not completed import or compilation.
`RELOAD_IMMINENT` is a conservative hint for the existing Python reconnect
handling; it does not mean that a reload will happen on every refresh.

1. Finish external writes, then call `refresh_assets` once.
2. Poll `get_compilation_status` with no arguments, approximately once a second:
   `{"jsonrpc":"2.0","id":"poll-1","method":"get_compilation_status","params":{}}`.
3. Continue on `PENDING`. Terminal results are:

| Prefix | Meaning |
| --- | --- |
| `SUCCESS:` | A compilation finished cleanly. |
| `FAILED:` | Latest compiler diagnostics or refresh exception, followed by details. Fix files externally, then refresh again. A no-op does not erase an earlier failure. |
| `NO_COMPILATION:` | Refresh settled without a new compiler result. Expected for no-op/non-code edits; **does not certify edited scripts**. If code was expected to compile, inspect Unity's Console and import/compilation settings. |
| `UNKNOWN:` | No result recorded in this Editor session. Never treat this as compilation success. |

An accepted refresh immediately invalidates an older `SUCCESS`. Status and
errors survive domain reload through `SessionState`, but not an Editor restart.
Status is global/latest, shared with `write_unity_script` and automatic Unity
compilation; the returned token is diagnostic, not a per-request polling API.
Coordinate writers so subsequent edits do not race with the refresh/poll cycle.
Compiler diagnostics remain stored until a new compilation supplies a result.
The existing write tool keeps its arguments and acknowledgement, and now also
settles writes that do not trigger compilation.

If Unity is compiling, importing, transitioning Play Mode, or another refresh
is pending, the result starts `BUSY:` and no new work is scheduled. Poll and
retry when idle. If Unity becomes busy between acceptance and the deferred
refresh callback, the callback records a `FAILED:` diagnostic asking for a retry
instead of overlapping import work. Application outcomes use the existing
JSON-RPC string `result`; transport/protocol errors retain existing conventions.

During domain reload the listener can disappear temporarily. Python's existing
`RELOAD_IMMINENT` handling retries connection failures for a 25-second grace
period on subsequent calls. Unity's existing auto-connect preference restores
the listener after reload. If reload takes longer, retry polling after Unity
settles; if needed use **Tools > Unity Semantic Bridge > Connect**. Do not replay
refresh merely because a poll lost its connection. Re-fetch scene hierarchy
before reusing Unity instance IDs after reload. A timeout is ambiguous: queued
requests are not cancelled by the current transport.

Requests still depend on `EditorApplication.update`, and deferred refresh uses
`EditorApplication.delayCall`. An unfocused/busy Editor can delay processing or
cause timeouts; focus Unity and retry polling if necessary. This tool does not
fix background Editor throttling. No live focus behavior was measured for this
change, and no transport or reconnect policy was changed.

Restart the **Python MCP server process** after updating to register the new tool,
and reconnect/reload the MCP client tool list if it caches discovery. Unity
must import/compile the updated local package once before its dispatcher knows
`refresh_assets`; this bootstrap cannot use a tool absent from the old assembly.

## Refresh validation

Automated Python checks (mock HTTP only, no Editor access):

```sh
cd mcp-editor-bridge
PYTHONDONTWRITEBYTECODE=1 .venv/bin/python -m unittest discover -s tests -v
```

These verify the no-argument FastMCP registration, method routing, status
pass-through, and refresh acknowledgement followed by a failed connection and
successful poll retry without replaying refresh.

`Tests/Editor/RefreshAssetsTests.cs` uses the package's existing Unity EditMode
NUnit setup. Its deterministic tests simulate compiler callbacks and idle
settling for no-op/non-code paths, successful compilation, failure/recovery,
busy rejection, stale callbacks, and refresh exceptions. They do not invoke
real imports, compile test scripts, or reload the domain. Run them via
**Window > General > Test Runner > EditMode** in an idle disposable test project.
Two additional opt-in (`Explicit`) Unity tests exercise the real dispatcher and
refresh callback for a no-op and a temporary text asset import. Run those only
in a disposable project: refresh can also import unrelated pending disk edits.
All Unity tests were added but were not run against the shared Editor.

Live integration checks remain separate and unperformed for this change:

- In a disposable project, request a no-op refresh and then import a new `.txt`
  asset; each should settle without indefinite `PENDING` or a forced reload.
- Edit a valid scratch C# script externally, refresh, poll, and verify a fresh
  clean compilation plus listener reconnection after Unity's reload.
- In that disposable project only, introduce a compiler error, inspect `FAILED`
  diagnostics, verify another no-op retains them, then fix and refresh to verify
  recovery. Never inject broken scripts into Astra Express.
- Check focused and unfocused request behavior separately and record delays.

Astra Express consumes this package via a local file dependency and shares its
Editor with another session. Coordinate with that session **before** any live
reload tests there. No Astra Express gameplay/assets were edited by this change.

## Editor throttling and preferences

`set_editor_throttling(mode)` takes one required string: `"no_throttling"`,
`"default"`, or `"restore"`. It changes **Preferences > General > Interaction
Mode** on Unity's main thread. `no_throttling` sets the stored idle time to 0 ms;
`default` removes the two preference overrides so Unity uses Default (4 ms).
No Throttling may increase CPU usage and power consumption. This affects
user-level Editor preferences, not project settings/assets, Play Mode, asset
refresh, or scene saving.

`restore` restores the mode and stored idle time captured before the first
successful override, including Custom/Monitor Refresh Rate and whether the
preference keys originally existed. Repeated `no_throttling`/`default` calls
retain that original restore point. A successful restore consumes it; another
restore returns `Error:` without changing preferences. A fresh override after
restore captures a new baseline. Restore overwrites any intervening manual
changes to these two preferences, so coordinate with other users of the Editor.
The saved restore point is shared by bridge clients in this Editor session.

The preference values persist, while the restore point uses `SessionState`:
it survives script/domain reload and Python MCP reconnect, but **not an Editor
restart**. Restore explicitly before quitting if the override is temporary;
there is no automatic restoration on disconnect or quit. Other Editor instances
may share the same user preference store.

Requests to `POST http://127.0.0.1:1073/rpc`:

```json
{"jsonrpc":"2.0","id":"throttle-1","method":"set_editor_throttling","params":{"mode":"no_throttling"}}
```

```json
{"jsonrpc":"2.0","id":"prefs-1","method":"get_project_settings","params":{"sections":["editor_prefs"]}}
```

```json
{"jsonrpc":"2.0","id":"restore-1","method":"set_editor_throttling","params":{"mode":"restore"}}
```

The setter returns a JSON **string** in the usual JSON-RPC `result`, whose
parsed contents look like:

```json
{
  "requestedMode": "no_throttling",
  "changed": true,
  "editor_prefs": {
    "interactionMode": "no_throttling",
    "interactionModeLabel": "No Throttling",
    "interactionModeRaw": 1,
    "applicationIdleTimeMs": 0,
    "restoreAvailable": true,
    "canSetInteractionMode": true,
    "scope": "editor_user"
  }
}
```

`get_project_settings(sections=["editor_prefs"])` returns the same `editor_prefs`
object, without `requestedMode`/`changed`. Omitting sections or passing an empty
list includes it with the existing sections. `interactionMode` reports
`default`, `no_throttling`, `monitor_refresh_rate`, `custom`, or `unknown` (with
the raw integer preserved). `applicationIdleTimeMs` is the stored preference,
not a measured update interval. `changed` reports whether the stored keys or
values changed; an idempotent override still keeps a restore point.

The implementation follows Unity's
[Preferences UI source](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/PreferencesWindow/PreferencesSettingsProviders.cs):
`EditorPrefs` plus the internal `EditorApplication.UpdateInteractionModeSettings`
method, accessed by reflection. If that method is unavailable, the setter
returns `Error:` without writing preferences and the getter reports
`canSetInteractionMode: false`. Invalid modes and unavailable/corrupt restore
points also return `Error:`. An apply failure attempts to roll back the previous
values and reports any rollback failure.

This does not bypass `EditorApplication.update`: a queued setter may itself
need Unity to be focused before it executes. No guarantee is made about
unfocused/minimized Editor responsiveness or OS background scheduling. Unity
[ignores this throttling preference in Play Mode](https://docs.unity3d.com/2022.3/Documentation/Manual/Preferences.html).

After installing the updated package, let Unity compile it and restart/resume
the Python MCP client/server to discover the new setter. The existing
`get_project_settings` tool gains the `editor_prefs` section.

Validation: Python tests cover tool schema, all three mode routes, the
`editor_prefs` filter, all-sections routing, and error pass-through. Unity
`EditorThrottlingReadTests` covers the section and invalid input;
`EditorThrottlingMutationTests` is opt-in (`Explicit`) because it temporarily
changes user preferences. It covers repeated overrides, original Custom/Monitor
restoration, absent keys, session snapshot restoration, and corrupt snapshots.
Run mutation tests only in a coordinated test Editor. Live preference changes,
actual domain reload restoration, and focus behavior were not tested in the
shared Editor for this change.
