"""Registration/transport contract; Unity behavior is covered by EditMode tests and the game's harness."""
import unittest
from unittest.mock import AsyncMock, patch
from fastmcp import FastMCP
import mcp_tools


class BakeLayoutTests(unittest.IsolatedAsyncioTestCase):
    async def test_path_is_optional_and_forwarded_only_when_given(self):
        server = FastMCP("test")
        mcp_tools.register_unity_tools(server)
        tool = await server.get_tool("bake_layout")
        self.assertNotIn("path", tool.parameters.get("required", []))
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = '{"v":1,"size":[12,12]}'
            self.assertEqual(await tool.fn(), call.return_value)
            call.assert_awaited_once_with("bake_layout", {})
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = "Wrote Assets/a.layout.json"
            await tool.fn("Assets/a.layout.json")
            call.assert_awaited_once_with("bake_layout", {"path": "Assets/a.layout.json"})
