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
