"""JSON-RPC 2.0 event server: Unity -> Python.

Listens on 127.0.0.1:1074/rpc (configurable via state_manager.app_state).
Handles JSON-RPC 2.0 single and batch requests.  All requests are
acknowledged (notifications without ``id`` receive a response with
``id`` = null for unified ``call_unity`` handling).

Usage:
    from event_server import register_handler, start_event_server

    def on_hierarchy_changed(params):
        print("hierarchy changed", params)

    register_handler("unity/hierarchyChanged", on_hierarchy_changed)
    start_event_server()  # daemon thread, non-blocking

Handlers may be sync or async (async is run via asyncio.run in the handler thread
or scheduled on the running loop if one exists).
"""
from __future__ import annotations

import asyncio
import json
import logging
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Callable, Dict, Optional

logger = logging.getLogger(__name__)

# method -> callable(params) -> result | None
_handlers: Dict[str, Callable[..., Any]] = {}
_handlers_lock = threading.Lock()
_server: Optional[ThreadingHTTPServer] = None
_server_thread: Optional[threading.Thread] = None

# JSON-RPC error codes
PARSE_ERROR = -32700
INVALID_REQUEST = -32600
METHOD_NOT_FOUND = -32601
INVALID_PARAMS = -32602
INTERNAL_ERROR = -32603


def register_handler(method: str, handler: Callable[..., Any]) -> None:
    """Register a handler for a JSON-RPC method.  `handler(params)` is called
    with the params object (dict/list/None).  Return value becomes the JSON-RPC result.
    If the handler raises, the error is returned as code -32603.
    """
    with _handlers_lock:
        _handlers[method] = handler
    logger.info(f"[event_server] registered handler for '{method}'")


def unregister_handler(method: str) -> None:
    with _handlers_lock:
        _handlers.pop(method, None)


def on_event(method: str):
    """Decorator: @on_event("unity/hierarchyChanged")"""
    def decorator(fn: Callable[..., Any]):
        register_handler(method, fn)
        return fn
    return decorator


def _get_handler(method: str) -> Optional[Callable[..., Any]]:
    with _handlers_lock:
        return _handlers.get(method)


async def _invoke_handler(handler: Callable[..., Any], params: Any) -> Any:
    """Support both sync and async handlers."""
    if asyncio.iscoroutinefunction(handler):
        return await handler(params)
    # plain callable – run in default executor if it might block? just call directly
    result = handler(params)
    if asyncio.iscoroutine(result):
        return await result
    return result


def _invoke_sync(handler: Callable[..., Any], params: Any) -> Any:
    """Invoke handler from the HTTP thread.  Bridges async if needed."""
    try:
        # If there's a running event loop in this thread, we can't asyncio.run
        # Instead, create a new loop.
        if asyncio.iscoroutinefunction(handler):
            return asyncio.run(handler(params))  # type: ignore
        result = handler(params)
        if asyncio.iscoroutine(result):
            return asyncio.run(result)  # type: ignore
        return result
    except Exception as e:
        # re-raise to be caught by caller and turned into JSON-RPC error
        raise


def _make_error(id_val: Any, code: int, message: str, data: Any = None) -> dict:
    err = {"code": code, "message": message}
    if data is not None:
        err["data"] = data
    return {"jsonrpc": "2.0", "id": id_val, "error": err}


def _make_result(id_val: Any, result: Any) -> dict:
    return {"jsonrpc": "2.0", "id": id_val, "result": result}


def _handle_single(obj: Any) -> dict:
    """Handle one JSON-RPC object.  Always returns a response (acknowledged)."""
    if not isinstance(obj, dict):
        return _make_error(None, INVALID_REQUEST, "Invalid Request: expected object")

    if obj.get("jsonrpc") != "2.0":
        return _make_error(obj.get("id"), INVALID_REQUEST, "Invalid Request: jsonrpc must be '2.0'")

    method = obj.get("method")
    if not isinstance(method, str):
        return _make_error(obj.get("id"), INVALID_REQUEST, "Invalid Request: missing or invalid 'method'")

    params = obj.get("params", None)
    # ``id`` may be missing (notification) — we still ack with ``id`` = None
    id_val = obj.get("id")

    handler = _get_handler(method)
    if handler is None:
        return _make_error(id_val, METHOD_NOT_FOUND, f"Method not found: {method}")

    try:
        result = _invoke_sync(handler, params)
    except Exception as e:
        logger.exception(f"[event_server] handler for '{method}' raised")
        return _make_error(id_val, INTERNAL_ERROR, f"Internal error: {e}", data=str(e))

    return _make_result(id_val, result if result is not None else "ok")


class _RpcHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        # Accept /rpc and /events and /mcp as aliases
        if self.path not in ("/rpc", "/events", "/mcp", "/jsonrpc"):
            self.send_response(404)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"error":"not found. Use POST /rpc"}')
            return

        length = int(self.headers.get("Content-Length", 0) or 0)
        raw = self.rfile.read(length).decode("utf-8") if length else ""

        if not raw.strip():
            resp = _make_error(None, INVALID_REQUEST, "Invalid Request: empty body")
            self._send_json(400, resp)
            return

        try:
            data = json.loads(raw)
        except json.JSONDecodeError as e:
            resp = _make_error(None, PARSE_ERROR, f"Parse error: {e.msg}")
            self._send_json(400, resp)
            return

        # Batch handling
        if isinstance(data, list):
            if len(data) == 0:
                resp = _make_error(None, INVALID_REQUEST, "Invalid Request: empty batch")
                self._send_json(400, resp)
                return
            responses = [_handle_single(item) for item in data]
            self._send_json(200, responses)
            return
        else:
            resp = _handle_single(data)
            # error responses may use appropriate HTTP code, but spec says 200 with error object.
            # Use 200 for method errors, 400 for parse/invalid.
            code = 200
            if "error" in resp:
                err_code = resp["error"].get("code")
                if err_code in (PARSE_ERROR, INVALID_REQUEST):
                    code = 400
                elif err_code == METHOD_NOT_FOUND:
                    code = 404
            self._send_json(code, resp)

    def do_GET(self):
        if self.path == "/health":
            self._send_json(200, {"status": "ok", "service": "python-event-server"})
            return
        self.send_response(404)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"error":"not found"}')

    def _send_json(self, http_code: int, obj: Any):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(http_code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format, *args):
        # Route to logger at debug level to avoid spamming
        logger.debug(f"[event_server] {self.client_address[0]} - {format % args}")


def start_event_server(host: str | None = None, port: int | None = None) -> Optional[ThreadingHTTPServer]:
    """Start the event server in a daemon thread.  Idempotent."""
    global _server, _server_thread

    if _server is not None:
        logger.info("[event_server] already running")
        return _server

    from state_manager import app_state

    h = host or app_state.python_event_host
    p = port if port is not None else app_state.python_event_port

    try:
        _server = ThreadingHTTPServer((h, p), _RpcHandler)
        _server.daemon_threads = True
        # Allow immediate reuse
        _server.allow_reuse_address = True
    except OSError as e:
        logger.error(f"[event_server] failed to bind {h}:{p}: {e}")
        _server = None
        return None

    def _serve():
        logger.info(f"[event_server] listening on http://{h}:{p}/rpc (POST JSON-RPC 2.0)")
        try:
            _server.serve_forever()
        except Exception as e:
            logger.error(f"[event_server] serve_forever error: {e}")

    _server_thread = threading.Thread(target=_serve, name="py-event-server", daemon=True)
    _server_thread.start()
    return _server


def stop_event_server() -> None:
    global _server, _server_thread
    if _server is not None:
        try:
            _server.shutdown()
            _server.server_close()
        except Exception:
            pass
        _server = None
        _server_thread = None
        logger.info("[event_server] stopped")
