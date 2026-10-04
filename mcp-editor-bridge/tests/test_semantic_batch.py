"""Typed schemas and transport; Editor behavior lives in SemanticBatchTests."""
import unittest
from unittest.mock import AsyncMock, patch
from pydantic import TypeAdapter, ValidationError
from fastmcp import FastMCP
from batch_requests import BatchLimits, BatchOperation, SetFieldsOperation
import httpx
import time
import unity_bridge
from state_manager import app_state
import mcp_tools


class SemanticBatchTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.server = FastMCP("batch-test")
        mcp_tools.register_unity_tools(self.server)

    async def test_batch_transport_is_one_request_and_never_retries(self):
        tool = await self.server.get_tool("semantic_batch")
        operations = [SetFieldsOperation(id="edit", op="set_fields", target="opaque", fields={"m_Enabled": True}, expected={"m_Enabled": False})]
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = '{"results":[{"id":"edit","status":"error"}]}'
            self.assertEqual(await tool.fn(operations, dry_run=True, name="Tune", limits=BatchLimits(maxTargets=2)), call.return_value)
            call.assert_awaited_once_with("semantic_batch", {
                "operations": [{"id": "edit", "op": "set_fields", "target": "opaque", "fields": {"m_Enabled": True}, "expected": {"m_Enabled": False}}],
                "dryRun": True, "name": "Tune", "limits": BatchLimits(maxTargets=2).model_dump(),
            }, retry_connect=False)

    async def test_prefab_query_and_reference_transport(self):
        tool = await self.server.get_tool("inspect_prefab_assets")
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = '{}'
            await tool.fn(["Assets/Rig.prefab"], "UnityEngine.CharacterJoint", ["m_Axis"])
            call.assert_awaited_once_with("inspect_prefab_assets", {"paths": ["Assets/Rig.prefab"], "component": "UnityEngine.CharacterJoint", "properties": ["m_Axis"]})
        tool = await self.server.get_tool("acquire_editor_references")
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            await tool.fn([123])
            call.assert_awaited_once_with("acquire_editor_references", {"instanceIds": [123]})

    async def test_schema_exposes_allowlist_and_typed_limits(self):
        tool = await self.server.get_tool("semantic_batch")
        schema = tool.parameters
        self.assertIn("operations", schema["required"])
        self.assertIn("InspectOperation", schema["$defs"])
        self.assertIn("SetFieldsOperation", schema["$defs"])
        self.assertEqual(schema["$defs"]["BatchLimits"]["properties"]["maxResponseBytes"]["maximum"], 1048576)
        self.assertFalse(schema["$defs"]["SetFieldsOperation"]["additionalProperties"])

    def test_invalid_operations_and_limits_fail_schema_validation(self):
        adapter = TypeAdapter(BatchOperation)
        for args in ({"id": "x", "op": "delete_gameobject", "target": "t"}, {"id": "x", "op": "inspect", "target": "t", "fields": {"x": 1}}):
            with self.assertRaises(ValidationError):
                adapter.validate_python(args)
        for args in ({"maxTargets": 513}, {"maxResponseBytes": 1024}, {"arbitrary": 1}):
            with self.assertRaises(ValidationError):
                BatchLimits(**args)


class MutationTransportTests(unittest.IsolatedAsyncioTestCase):
    async def test_mutation_connection_failure_is_not_retried_during_reload_grace(self):
        prior = getattr(app_state, "last_reload_trigger_at", 0)
        app_state.last_reload_trigger_at = time.time()
        try:
            with patch.object(unity_bridge.httpx.AsyncClient, "post", new_callable=AsyncMock) as post:
                post.side_effect = httpx.ConnectError("disconnected")
                result = await unity_bridge.call_unity("semantic_batch", {"operations": []}, retry_connect=False)
                self.assertTrue(result.startswith("Error:"))
                self.assertEqual(post.await_count, 1)
        finally:
            app_state.last_reload_trigger_at = prior
