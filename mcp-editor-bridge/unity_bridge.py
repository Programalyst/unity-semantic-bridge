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
        except json.JSONDecodeError:
            return resp.text  # Defensive fallback for an unexpected non-JSON response

        # Unity side EditorBridge will always JSON wrap response in "content"
        if isinstance(data, dict) and "content" in data:
            return str(data["content"])

        return f"Error: Unexpected Unity response: {data}"
    