"""MCP schema and routing; Editor behavior is tested by UiAuthoringTests."""
import unittest
from unittest.mock import AsyncMock, patch
from fastmcp import FastMCP
import mcp_tools


class UiToolsTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.server = FastMCP("test")
        mcp_tools.register_unity_tools(self.server)

    async def test_defaults_and_routes(self):
        cases = [
            ("author_ui", {"operations": [{"op": "create"}]}, {"operations": [{"op": "create"}], "name": "Author uGUI"}),
            ("inspect_ui", {"instance_id": 7}, {"instanceId": 7, "maxNodes": 100, "includeReferences": False}),
            ("raycast_ui", {"x": 12.5, "y": 20}, {"x": 12.5, "y": 20}),
            ("undo_ui_batch", {"undo_token": "batch-id"}, {"undoToken": "batch-id"}),
            ("save_ui_context", {"instance_id": 7}, {"instanceId": 7}),
        ]
        for name, args, payload in cases:
            with self.subTest(tool=name):
                tool = await self.server.get_tool(name)
                with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
                    call.return_value = "Error: checked by Unity"
                    self.assertEqual(await tool.fn(**args), call.return_value)
                    call.assert_awaited_once_with(name, payload)
                self.assertTrue(tool.parameters["required"])

    async def test_explicit_details_and_save_path(self):
        cases = [
            ("inspect_ui", {"instance_id": 2, "camera_instance_id": 3, "max_nodes": 8, "include_references": True},
             {"instanceId": 2, "cameraInstanceId": 3, "maxNodes": 8, "includeReferences": True}),
            ("raycast_ui", {"x": 0, "y": 1, "camera_instance_id": 3}, {"x": 0, "y": 1, "cameraInstanceId": 3}),
            ("save_ui_context", {"instance_id": 2, "path": "Assets/UI/Test.unity"}, {"instanceId": 2, "path": "Assets/UI/Test.unity"}),
        ]
        for name, args, payload in cases:
            tool = await self.server.get_tool(name)
            with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
                await tool.fn(**args)
                call.assert_awaited_once_with(name, payload)
