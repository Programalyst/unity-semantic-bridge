"""Transport/registration tests only: never contacts a running Unity Editor."""
import asyncio
import inspect
import json
import unittest
from unittest.mock import AsyncMock, patch

from fastmcp import FastMCP
import mcp_tools
import unity_bridge
from state_manager import app_state


def _pending(kind="enter"):
    return json.dumps({
        "isPlaying": False, "transitioning": True,
        "isCompiling": False, "isUpdating": False,
        "pendingOp": {"kind": kind, "token": "abc", "expiresUtc": "2030-01-01T00:00:00Z"},
    })


def _settled(playing):
    return json.dumps({
        "isPlaying": playing, "transitioning": False,
        "isCompiling": False, "isUpdating": False,
        "pendingOp": None,
    })


class PlayModeToolsTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        app_state.unity_request_lock = asyncio.Lock()
        app_state.last_reload_trigger_at = 0
        server = FastMCP("test")
        mcp_tools.register_unity_tools(server)
        self.tools = {name: await server.get_tool(name) for name in (
            "open_scene", "poll_play_state", "enter_play", "exit_play")}

    async def test_tools_registered_with_expected_params(self):
        self.assertEqual(set(inspect.signature(self.tools["open_scene"].fn).parameters),
                         {"path", "dirty_policy"})
        self.assertEqual(len(inspect.signature(self.tools["poll_play_state"].fn).parameters), 0)
        for name in ("enter_play", "exit_play"):
            self.assertEqual(list(inspect.signature(self.tools[name].fn).parameters), ["timeout_s"])

    async def test_open_scene_and_poll_param_mapping(self):
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = '{"status":"ok"}'
            self.assertEqual(
                await self.tools["open_scene"].fn(path="Assets/Scenes/GridBattle.unity"),
                '{"status":"ok"}')
            call.assert_awaited_once_with(
                "open_scene", {"path": "Assets/Scenes/GridBattle.unity", "dirty_policy": "fail"})
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = '{"isPlaying":false}'
            self.assertEqual(await self.tools["poll_play_state"].fn(), '{"isPlaying":false}')
            call.assert_awaited_once_with("poll_play_state", {})

    async def test_enter_play_terminal_result_passes_through(self):
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = '{"status":"ok","kind":"enter"}'
            self.assertEqual(await self.tools["enter_play"].fn(timeout_s=30),
                             '{"status":"ok","kind":"enter"}')
            self.assertEqual(call.await_count, 1)

    async def test_enter_play_clamps_timeout_to_transport_window(self):
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = '{"status":"ok"}'
            await self.tools["enter_play"].fn(timeout_s=1000)
            call.assert_awaited_once_with("enter_play", {"timeout_s": 55.0},
                                          timeout=60.0, retry_connect=False)

    async def test_enter_play_falls_back_to_poll_after_reload_disconnect(self):
        responses = ["Error: Unity Editor is not connected to the bridge. Start the HTTP listener.",
                     _pending("enter"), _settled(True)]
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call, \
                patch.object(mcp_tools.asyncio, "sleep", new_callable=AsyncMock):
            call.side_effect = responses
            result = await self.tools["enter_play"].fn(timeout_s=30)
        self.assertIn("settled via poll", result)
        self.assertIn('"isPlaying": true', result)
        methods = [c.args[0] for c in call.await_args_list]
        self.assertEqual(methods, ["enter_play", "poll_play_state", "poll_play_state"])

    async def test_exit_play_poll_timeout_reports_last_state(self):
        now = [1000.0]
        notifications = {"sleeps": 0}

        async def fake_sleep(_):
            notifications["sleeps"] += 1
            now[0] += 1.0

        responses = ["Error: Unity timed out processing the request."] + [_pending("exit")] * 10
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call, \
                patch("time.time", side_effect=lambda: now[0]), \
                patch.object(mcp_tools.asyncio, "sleep", side_effect=fake_sleep):
            call.side_effect = responses
            result = await self.tools["exit_play"].fn(timeout_s=2)
        self.assertTrue(result.startswith("TIMEOUT after 2s"))
        self.assertIn("pendingOp", result)
        self.assertIn("no_throttling", result)
        self.assertGreaterEqual(notifications["sleeps"], 2)

    async def test_server_instructions_mandate_no_throttling(self):
        from main import mcp as server
        instructions = getattr(server, "instructions", "") or ""
        self.assertIn("no_throttling", instructions)
        self.assertIn("Unless the user prefers otherwise", instructions)

    async def test_busy_result_is_terminal_not_polled(self):
        with patch.object(mcp_tools, "call_unity", new_callable=AsyncMock) as call:
            call.return_value = "BUSY: play-mode op 'enter' (token=abc) already in progress."
            result = await self.tools["enter_play"].fn(timeout_s=30)
            self.assertTrue(result.startswith("BUSY:"))
            self.assertEqual(call.await_count, 1)


if __name__ == "__main__":
    unittest.main()
