Bridge uses JSON-RPC 2.0 over HTTP.

- Python -> Unity: `POST http://127.0.0.1:1073/rpc` with `{"jsonrpc":"2.0","id":"...","method":"...","params":{...}}` -> `{"jsonrpc":"2.0","id":"...","result":"..."}`.
- Unity -> Python events: `POST http://127.0.0.1:1074/rpc` with JSON-RPC notifications/requests (e.g. `{"jsonrpc":"2.0","method":"unity/hierarchyChanged","params":{...}}`). Handled by `event_server.py` (health: `GET /health` on both ends).
