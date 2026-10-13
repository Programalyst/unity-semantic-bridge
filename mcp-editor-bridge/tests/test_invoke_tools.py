"""Transport/registration tests only: never contacts a running Unity Editor."""
import asyncio
import inspect
import unittest
from unittest.mock import AsyncMock, patch

from fastmcp import FastMCP
import mcp_tools
from state_manager import app_state


class InvokeToolsTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        app_state.unity_request_lock = asyncio.Lock()
        app_state.last_reload_trigger_at = 0
        server = FastMCP("test")
        mcp_tools.register_unity_tools(server)
        self.menu = await server.get_tool("invoke_menu_item")
        self.static = await server.get_tool("invoke_static")

    async def test_signatures(self):
        self.assertEqual(list(inspect.signature(self.menu.fn).parameters), ["menu_path"])
        self.assertEqual(list(inspect.signature(self.static.fn).parameters),
                         ["type_name", "method_name"])

    async def test_menu_param_mapping_and_no_replay(self):
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = "ok"
            self.assertEqual(await self.menu.fn(menu_path="Tools/Grid Combat/Validate Action Points"), "ok")
            call.assert_awaited_once_with("invoke_menu_item",
                                          {"menuPath": "Tools/Grid Combat/Validate Action Points"},
                                          retry_connect=False)

    async def test_static_param_mapping_and_no_replay(self):
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = "pong"
            self.assertEqual(await self.static.fn(type_name="A.B", method_name="Ping"), "pong")
            call.assert_awaited_once_with("invoke_static",
                                          {"typeName": "A.B", "methodName": "Ping"},
                                          retry_connect=False)

    async def test_errors_pass_through(self):
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = "Error: Menu item not found: 'Tools/Nope'."
            self.assertTrue((await self.menu.fn(menu_path="Tools/Nope")).startswith("Error:"))
            call.return_value = "Error: Type not found: 'Nope'."
            self.assertTrue((await self.static.fn(type_name="Nope", method_name="Nope")).startswith("Error:"))


if __name__ == "__main__":
    unittest.main()
