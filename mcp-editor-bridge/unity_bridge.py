"""JSON-RPC 2.0 client for Unity EditorBridge.

Python -> Unity (request):
  {"jsonrpc":"2.0","id":"<uuid>","method":"Get_SceneHierarchy","params":{"depth":2,...}}

Unity -> Python (response):
  {"jsonrpc":"2.0","id":"<uuid>","result":"<string result>"}
  or {"jsonrpc":"2.0","id":"<uuid>","error":{"code":-32000,"message":"..."}}

All calls use ``call_unity`` and are acknowledged — notifications are not
used. Unity events (Unity -> Python) also use ``call_unity``-style requests
and ``event_server.py`` (127.0.0.1:<event port>/rpc, default 1074) acknowledges them.
"""
import logging
import json
import httpx
import asyncio
import time
import uuid
from typing import Any, Dict, Optional

from state_manager import app_state

logger = logging.getLogger(__name__)

def _not_connected_msg() -> str:
    return (f"Error: Unity Editor is not connected to the bridge at {app_state.unity_rpc_url}. "
            "Start the HTTP listener via Tools > Unity Semantic Bridge > Start HTTP Listener, and check that the "
            "project's ports there match this MCP server's --unity-port/--event-port.")
RECONNECT_GRACE_SECONDS = 25.0
POLL_INTERVAL = 1.0
JSONRPC_VERSION = "2.0"


async def call_unity(method: str, params: Optional[Dict[str, Any]] = None, *, timeout: float = 30.0, retry_connect: bool = True) -> str:
    """JSON-RPC request to Unity. Returns a result/error string.

    retry_connect=False disables reload-grace connection retries for mutation batches.
    Requests are never retried after a response or a timeout.
    """
    req_id = str(uuid.uuid4())
    payload: Dict[str, Any] = {
        "jsonrpc": JSONRPC_VERSION,
        "id": req_id,
        "method": method,
        "params": params if params is not None else {},
    }
    return await _send_jsonrpc(payload, timeout=timeout, retry_connect=retry_connect)


async def _send_jsonrpc(payload: Dict[str, Any], *, timeout: float = 30.0, retry_connect: bool = True) -> str:
    """POST a JSON-RPC request to Unity and return the result string."""
    url = app_state.unity_rpc_url

    async with app_state.unity_request_lock:
        recently_triggered = (
            retry_connect
            and getattr(app_state, "last_reload_trigger_at", 0)
            and time.time() - app_state.last_reload_trigger_at < RECONNECT_GRACE_SECONDS
        )
        deadline = time.time() + (RECONNECT_GRACE_SECONDS if recently_triggered else 0)

        resp: Optional[httpx.Response] = None
        while True:
            try:
                async with httpx.AsyncClient(timeout=timeout) as client:
                    resp = await client.post(url, json=payload)
                break
            except httpx.ConnectError:
                if time.time() >= deadline:
                    return _not_connected_msg()
                await asyncio.sleep(POLL_INTERVAL)
                continue
            except httpx.TimeoutException:
                return "Error: Unity timed out responding to the request."

        if resp is None:
            return _not_connected_msg()

        if resp.status_code >= 400:
            try:
                data = resp.json()
                if isinstance(data, dict) and "error" in data:
                    err = data["error"]
                    msg = err.get("message", str(err)) if isinstance(err, dict) else str(err)
                    return f"Error: {msg}"
            except Exception:
                pass
            return f"Error: Unity returned HTTP {resp.status_code}: {resp.text[:500]}"

        if "RELOAD_IMMINENT" in resp.text:
            app_state.last_reload_trigger_at = time.time()

        try:
            data = resp.json()
        except json.JSONDecodeError:
            return resp.text

        if isinstance(data, dict):
            if "error" in data:
                err = data["error"]
                if isinstance(err, dict):
                    msg = err.get("message", json.dumps(err))
                    code = err.get("code", "")
                    return f"Error: {msg} (code {code})" if code != "" else f"Error: {msg}"
                return f"Error: {err}"
            if "result" in data:
                return str(data["result"])

        return f"Error: Unexpected Unity response: {data}"
