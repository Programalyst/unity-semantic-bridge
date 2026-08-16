import logging
import json
import httpx
from state_manager import app_state

logger = logging.getLogger(__name__)

_UNITY_NOT_CONNECTED_MSG = "Error: Unity Editor is not connected to the bridge. Start the HTTP listener via Tools > Unity Semantic Bridge > Connect."

async def send_to_unity(payload: dict) -> str:
    """POST payload JSON to Unity's HTTP bridge and return the content string."""
    url = f"{app_state.unity_base_url}/mcp"
    async with app_state.unity_request_lock:
        try:
            async with httpx.AsyncClient(timeout=30.0) as client:
                resp = await client.post(url, json=payload)
        except httpx.ConnectError:
            return _UNITY_NOT_CONNECTED_MSG
        except httpx.TimeoutException:
            return "Error: Unity timed out responding to the request."

        if resp.status_code == 429:
            return "Error: Unity is busy processing another request. Try again shortly."
        if resp.status_code == 503:
            return _UNITY_NOT_CONNECTED_MSG
        if resp.status_code >= 400:
            return f"Error: Unity returned HTTP {resp.status_code}: {resp.text[:500]}"

        try:
            data = resp.json()
        except Exception:
            # Fallback to raw text if Unity returned plain string
            return resp.text

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
    