"""MCP registration and routing checks; never changes live Editor preferences."""
import unittest
from unittest.mock import AsyncMock, patch

from fastmcp import FastMCP
import mcp_tools


class EditorThrottlingTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.server = FastMCP("test")
        mcp_tools.register_unity_tools(self.server)

    async def test_required_mode_enum_and_routing(self):
        tool = await self.server.get_tool("set_editor_throttling")
        self.assertEqual(tool.parameters["required"], ["mode"])
        self.assertEqual(tool.parameters["properties"]["mode"]["enum"],
                         ["no_throttling", "default", "restore"])
        for mode in ("no_throttling", "default", "restore"):
            with self.subTest(mode=mode), patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
                call.return_value = '{"requestedMode":"' + mode + '"}'
                self.assertEqual(await tool.fn(mode), call.return_value)
                call.assert_awaited_once_with("set_editor_throttling", {"mode": mode})

    async def test_editor_prefs_section_and_all_sections(self):
        tool = await self.server.get_tool("get_project_settings")
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = '{"editor_prefs":{"interactionMode":"default"}}'
            self.assertEqual(await tool.fn([" Editor_Prefs "]), call.return_value)
            call.assert_awaited_once_with("get_project_settings", {"sections": ["editor_prefs"]})
            call.reset_mock()
            await tool.fn()
            call.assert_awaited_once_with("get_project_settings", {})

    async def test_errors_pass_through(self):
        tool = await self.server.get_tool("set_editor_throttling")
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = "Error: No previous Interaction Mode is saved in this Editor session."
            self.assertEqual(await tool.fn("restore"), call.return_value)


if __name__ == "__main__":
    unittest.main()
