"""Transport/registration tests only: never contacts a running Unity Editor."""
import asyncio
import inspect
import json
import unittest
from unittest.mock import AsyncMock, patch

import httpx
from fastmcp import FastMCP
import mcp_tools
import unity_bridge
from state_manager import app_state


class RefreshAssetsTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        app_state.unity_request_lock = asyncio.Lock()
        app_state.last_reload_trigger_at = 0

    async def test_no_argument_tool_registration_and_route(self):
        server = FastMCP("test")
        mcp_tools.register_unity_tools(server)
        tool = await server.get_tool("refresh_assets")
        self.assertEqual(tool.parameters.get("properties", {}), {})
        self.assertEqual(len(inspect.signature(tool.fn).parameters), 0)
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = "Refresh queued. RELOAD_IMMINENT"
            self.assertEqual(await tool.fn(), call.return_value)
            call.assert_awaited_once_with("refresh_assets")

    async def test_refresh_then_poll_reconnects_without_replaying_refresh(self):
        requests = []
        attempts = 0

        def respond(request):
            nonlocal attempts
            payload = json.loads(request.content)
            requests.append(payload)
            if payload["method"] == "refresh_assets":
                self.assertEqual(payload["params"], {})
                result = "Refresh queued (token=test). RELOAD_IMMINENT: Unity may compile/reload if needed."
            else:
                attempts += 1
                if attempts == 1:
                    raise httpx.ConnectError("domain reload", request=request)
                result = "SUCCESS: compiled cleanly."
            return httpx.Response(200, json={"jsonrpc": "2.0", "id": payload["id"], "result": result})

        client_type = httpx.AsyncClient
        with patch.object(unity_bridge.httpx, "AsyncClient", side_effect=lambda **kw: client_type(
            transport=httpx.MockTransport(respond), **kw
        )), patch.object(unity_bridge.asyncio, "sleep", new_callable=AsyncMock):
            await unity_bridge.call_unity("refresh_assets")
            self.assertGreater(app_state.last_reload_trigger_at, 0)
            self.assertEqual(await unity_bridge.call_unity("get_compilation_status"), "SUCCESS: compiled cleanly.")
        self.assertEqual([r["method"] for r in requests],
                         ["refresh_assets", "get_compilation_status", "get_compilation_status"])
        self.assertEqual(requests[1]["id"], requests[2]["id"])

    async def test_status_and_busy_results_pass_through(self):
        client_type = httpx.AsyncClient
        for result in ("NO_COMPILATION: refresh completed", "PENDING: compiling",
                       "FAILED:\nProbe.cs:1 error", "SUCCESS: compiled cleanly.",
                       "BUSY: Unity is compiling"):
            with self.subTest(result=result), patch.object(
                unity_bridge.httpx, "AsyncClient", side_effect=lambda **kw: client_type(
                    transport=httpx.MockTransport(lambda request: httpx.Response(
                        200, json={"jsonrpc": "2.0", "id": "test", "result": result})), **kw)):
                self.assertEqual(await unity_bridge.call_unity("get_compilation_status"), result)
                self.assertEqual(app_state.last_reload_trigger_at, 0)


if __name__ == "__main__":
    unittest.main()
