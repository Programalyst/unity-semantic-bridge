"""Registration/transport contract; Unity behavior is covered by EditMode tests."""
import unittest
from unittest.mock import AsyncMock, patch
from fastmcp import FastMCP
import mcp_tools


class ModelInspectionTests(unittest.IsolatedAsyncioTestCase):
    async def test_registration_defaults_and_explicit_detail(self):
        server = FastMCP("test")
        mcp_tools.register_unity_tools(server)
        tool = await server.get_tool("inspect_model_asset")
        self.assertEqual(tool.parameters["required"], ["path"])
        self.assertFalse(tool.parameters["properties"]["include_details"]["default"])
        for details in (False, True):
            with self.subTest(details=details), patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
                call.return_value = "Model: Assets/Bullet.fbx"
                args = {"include_details": True} if details else {}
                self.assertEqual(await tool.fn("Assets/Bullet.fbx", **args), call.return_value)
                call.assert_awaited_once_with("inspect_model_asset", {"path": "Assets/Bullet.fbx", "include_details": details})

    async def test_actionable_error_passes_through(self):
        server = FastMCP("test")
        mcp_tools.register_unity_tools(server)
        tool = await server.get_tool("inspect_model_asset")
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = "Error: No imported asset at 'Assets/Missing.fbx'. Check the path and connected project; import the file in Unity first."
            self.assertEqual(await tool.fn("Assets/Missing.fbx"), call.return_value)
