import logging
import json
import httpx
import asyncio
import time
from state_manager import app_state

logger = logging.getLogger(__name__)

_UNITY_NOT_CONNECTED_MSG = "Error: Unity Editor is not connected to the bridge. Start the HTTP listener via Tools > Unity Semantic Bridge > Connect."
RECONNECT_GRACE_SECONDS = 25.0
POLL_INTERVAL = 1.0

async def send_to_unity(payload: dict) -> str:
    """POST payload JSON to Unity's HTTP bridge and return the content string."""
    url = f"{app_state.unity_base_url}/mcp"

    async with app_state.unity_request_lock:
        recently_triggered = (
            getattr(app_state, "last_reload_trigger_at", 0)
            and time.time() - app_state.last_reload_trigger_at < RECONNECT_GRACE_SECONDS
        )
        deadline = time.time() + (RECONNECT_GRACE_SECONDS if recently_triggered else 0)

        while True:
            try:
                async with httpx.AsyncClient(timeout=30.0) as client:
                    resp = await client.post(url, json=payload)
                break  # got a response, fall through to normal handling below
            except httpx.ConnectError:
                if time.time() >= deadline:
                    return _UNITY_NOT_CONNECTED_MSG
                await asyncio.sleep(POLL_INTERVAL)
                continue  # retry within the grace window
            except httpx.TimeoutException:
                return "Error: Unity timed out responding to the request."

        if resp.status_code >= 400:
            return f"Error: Unity returned HTTP {resp.status_code}: {resp.text[:500]}"

        if "RELOAD_IMMINENT" in resp.text:
            app_state.last_reload_trigger_at = time.time()

        try:
            data = resp.json()
        except Exception:
            return resp.text  # Fallback to raw text if Unity returned plain string

        # Unity may return {"content": "..."} or just a raw string/JSON
        if isinstance(data, dict) and "content" in data:
            content = data["content"]

            # Preserve JSON strings as-is; serialize dicts/lists
            if isinstance(content, str):
                return content
            return json.dumps(content) if isinstance(content, (dict, list)) else str(content)
        # If no content wrapper, return serialized body
        if isinstance(data, str):
            return data
        return json.dumps(data) if isinstance(data, (dict, list)) else str(data)
    