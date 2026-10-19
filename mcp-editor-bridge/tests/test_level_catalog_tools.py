"""Registration/transport contract for the level catalog; card behavior is covered by EditMode tests."""
import unittest
from unittest.mock import AsyncMock, patch
from fastmcp import FastMCP
import mcp_tools


class LevelCatalogToolTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.server = FastMCP("test")
        mcp_tools.register_unity_tools(self.server)

    async def test_catalog_defaults_to_assets_and_forwards_level(self):
        tool = await self.server.get_tool("generate_asset_catalog")
        self.assertNotIn("catalog", tool.parameters.get("required", []))
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn()
            call.assert_awaited_once_with("generate_asset_catalog", {"thumbnails": True, "catalog": "assets"})
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn(["Assets/Synty/PolygonApocalypseWasteland"], False, "level")
            call.assert_awaited_once_with("generate_asset_catalog", {
                "thumbnails": False, "catalog": "level", "scan_roots": ["Assets/Synty/PolygonApocalypseWasteland"]})

    async def test_describe_level_cards_forwards_paths_and_cell_size(self):
        tool = await self.server.get_tool("describe_level_cards")
        self.assertIn("paths", tool.parameters.get("required", []))
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn(["Assets/a.prefab"])
            call.assert_awaited_once_with("describe_level_cards", {"paths": ["Assets/a.prefab"], "cell_size": 2.0})

    async def test_query_level_catalog_forwards_only_given_filters(self):
        tool = await self.server.get_tool("query_level_catalog")
        self.assertEqual(tool.parameters.get("required", []), [])
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn()
            call.assert_awaited_once_with("query_level_catalog", {"source": "auto", "limit": 50, "offset": 0}, timeout=60.0)
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn(role=["cover_half"], cover="Half", max_cells=[2, 1], name="Sandbag", source="reviewed", cell_size=2.0)
            call.assert_awaited_once_with("query_level_catalog", {
                "source": "reviewed", "limit": 50, "offset": 0, "role": ["cover_half"], "cover": "Half",
                "max_cells": [2, 1], "name": "Sandbag", "cell_size": 2.0}, timeout=60.0)


if __name__ == "__main__":
    unittest.main()
