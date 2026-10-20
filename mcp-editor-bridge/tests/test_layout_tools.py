"""Registration/transport contract; Unity behavior is covered by EditMode tests and the game's harness."""
import base64
import json
import unittest
from unittest.mock import AsyncMock, patch
from fastmcp import Client, FastMCP
import mcp_tools


class LayoutToolTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.server = FastMCP("test")
        mcp_tools.register_unity_tools(self.server)

    async def tool(self, name):
        return await self.server.get_tool(name)

    async def test_bake_path_and_scene_are_optional_and_forwarded_only_when_given(self):
        tool = await self.tool("bake_layout")
        self.assertEqual(tool.parameters.get("required", []), [])
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = '{"v":1,"size":[12,12]}'
            self.assertEqual(await tool.fn(), call.return_value)
            call.assert_awaited_once_with("bake_layout", {})
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = "Wrote Assets/a.layout.json"
            await tool.fn("Assets/a.layout.json", "Assets/Scenes/A.unity")
            call.assert_awaited_once_with("bake_layout", {"path": "Assets/a.layout.json", "scene": "Assets/Scenes/A.unity"})

    async def test_get_layout_requires_path(self):
        tool = await self.tool("get_layout")
        self.assertIn("path", tool.parameters["required"])
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn("Assets/a.layout.json")
            call.assert_awaited_once_with("get_layout", {"path": "Assets/a.layout.json"})

    async def test_set_layout_forwards_layout_and_never_retries_the_write(self):
        tool = await self.tool("set_layout")
        self.assertEqual(sorted(tool.parameters["required"]), ["layout", "path"])
        layout = {"v": 1, "size": [4, 4]}
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn("Assets/a.layout.json", layout)
            call.assert_awaited_once_with("set_layout", {
                "path": "Assets/a.layout.json", "layout": layout, "validate": True, "max_lines": 40, "ascii": False,
                "metrics": False,
            }, timeout=60.0, retry_connect=False)

    async def test_validate_layout_defaults_to_the_grid_tier(self):
        tool = await self.tool("validate_layout")
        self.assertEqual(tool.parameters.get("required", []), [])
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn(path="Assets/a.layout.json")
            call.assert_awaited_once_with("validate_layout", {
                "tier": "grid", "ascii": False, "max_lines": 40, "metrics": False, "metrics_only": False,
                "path": "Assets/a.layout.json"}, timeout=60.0)
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn(tier="scene", scene="Assets/Scenes/A.unity", ascii=True, max_lines=0)
            call.assert_awaited_once_with("validate_layout", {
                "tier": "scene", "ascii": True, "max_lines": 0, "metrics": False, "metrics_only": False,
                "scene": "Assets/Scenes/A.unity"}, timeout=60.0)

    async def test_validate_layout_forwards_metrics_and_region(self):
        tool = await self.tool("validate_layout")
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn(path="Assets/a.layout.json", metrics=True, region=[0, 0, 9, 9])
            call.assert_awaited_once_with("validate_layout", {
                "tier": "grid", "ascii": False, "max_lines": 40, "metrics": True, "metrics_only": False,
                "region": [0, 0, 9, 9], "path": "Assets/a.layout.json"}, timeout=60.0)

    async def test_apply_layout_forwards_and_never_retries(self):
        tool = await self.tool("apply_layout")
        self.assertEqual(tool.parameters.get("required", []), [])
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn(layout='{"v":1}', scene="Assets/Scenes/A.unity", validate=False)
            call.assert_awaited_once_with("apply_layout", {
                "validate": False, "ascii": False, "max_lines": 40, "layout": '{"v":1}', "scene": "Assets/Scenes/A.unity",
            }, timeout=60.0, retry_connect=False)

    async def test_create_layout_scene_requires_template_and_path_and_never_retries(self):
        tool = await self.tool("create_layout_scene")
        self.assertEqual(sorted(tool.parameters["required"]), ["path", "template"])
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn("Assets/Scenes/T.unity", "Assets/Scenes/New.unity")
            call.assert_awaited_once_with("create_layout_scene", {
                "template": "Assets/Scenes/T.unity", "path": "Assets/Scenes/New.unity"}, timeout=60.0, retry_connect=False)
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn("Assets/Scenes/T.unity", "Assets/Scenes/New.unity", "Assets/Levels/New.asset", [16, 12])
            call.assert_awaited_once_with("create_layout_scene", {
                "template": "Assets/Scenes/T.unity", "path": "Assets/Scenes/New.unity",
                "level": "Assets/Levels/New.asset", "size": [16, 12]}, timeout=60.0, retry_connect=False)

    async def test_render_returns_caption_and_image(self):
        tool = await self.tool("render_layout_topdown")
        jpeg = b"\xff\xd8\xff\xd9"
        reply = json.dumps({"image": base64.b64encode(jpeg).decode(), "caption": "Top-down"})
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = reply
            caption, image = await tool.fn(cells=[0, 0, 3, 3], cut_height=2.4)
            call.assert_awaited_once_with("render_layout_topdown", {
                "margin_cells": 1, "grid": True, "markers": True, "max_width": 1280, "jpg_quality": 70,
                "cells": [0, 0, 3, 3], "cut_height": 2.4}, timeout=60.0)
        self.assertEqual(caption, "Top-down")
        self.assertEqual(image.data, jpeg)

    async def test_render_returns_text_and_image_content_over_mcp(self):
        jpeg = b"\xff\xd8\xff\xd9"
        reply = json.dumps({"image": base64.b64encode(jpeg).decode(), "caption": "Top-down"})
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = reply
            async with Client(self.server) as client:
                result = await client.call_tool("render_layout_topdown", {"cells": [0, 0, 3, 3]})
        self.assertEqual([c.type for c in result.content], ["text", "image"])
        self.assertEqual(result.content[0].text, "Top-down")
        self.assertEqual(base64.b64decode(result.content[1].data), jpeg)

    async def test_render_raises_on_an_error_reply(self):
        tool = await self.tool("render_layout_topdown")
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = "Error: no board"
            with self.assertRaises(RuntimeError):
                await tool.fn()


if __name__ == "__main__":
    unittest.main()
