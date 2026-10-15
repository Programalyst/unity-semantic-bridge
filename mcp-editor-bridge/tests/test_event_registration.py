"""Shared event port: free-port fallback, registration renewals and diagnostics.
Never contacts a running Unity Editor."""
import socket
import unittest
from unittest.mock import AsyncMock, patch

from fastmcp import FastMCP
import mcp_tools
from events import event_server, registration


def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class FallbackBindTests(unittest.TestCase):
    def tearDown(self):
        event_server.stop_event_server()

    def test_free_event_port_is_bound_as_primary(self):
        port = _free_port()
        self.assertIsNotNone(event_server.start_event_server(port=port))
        self.assertTrue(event_server.is_primary())
        self.assertEqual(event_server.bound_url(), f"http://127.0.0.1:{port}/rpc")

    def test_taken_event_port_falls_back_to_a_free_port(self):
        with socket.socket() as other:
            other.bind(("127.0.0.1", 0))
            other.listen()
            taken = other.getsockname()[1]
            self.assertIsNotNone(event_server.start_event_server(port=taken))
            self.assertFalse(event_server.is_primary())
            self.assertNotEqual(event_server.bound_url(), f"http://127.0.0.1:{taken}/rpc")

    def test_without_fallback_a_taken_port_leaves_no_server(self):
        with socket.socket() as other:
            other.bind(("127.0.0.1", 0))
            other.listen()
            self.assertIsNone(event_server.start_event_server(port=other.getsockname()[1], fallback_to_free_port=False))
            self.assertIsNone(event_server.bound_url())

    def test_stop_clears_bound_url(self):
        event_server.start_event_server(port=_free_port())
        event_server.stop_event_server()
        self.assertIsNone(event_server.bound_url())
        self.assertFalse(event_server.is_primary())


class RegistrationTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        registration.status = registration.RegistrationStatus()
        self.port = _free_port()
        event_server.start_event_server(port=self.port)

    def tearDown(self):
        event_server.stop_event_server()
        registration.status = registration.RegistrationStatus()

    async def test_registers_bound_url_with_ttl(self):
        with patch("unity_bridge.call_unity", AsyncMock(return_value='{"registered":"x","listeners":1}')) as call:
            self.assertTrue(await registration.register_once())
        method, params = call.await_args.args
        self.assertEqual(method, "register_event_listener")
        self.assertEqual(params, {"url": f"http://127.0.0.1:{self.port}/rpc", "ttlSeconds": registration.TTL_SECONDS})
        self.assertFalse(call.await_args.kwargs["retry_connect"])
        self.assertIsNotNone(registration.status.last_ok_at)

    async def test_disconnected_unity_is_recorded_and_retried(self):
        with patch("unity_bridge.call_unity", AsyncMock(return_value="Error: Unity Editor is not connected to the bridge")):
            self.assertFalse(await registration.register_once())
        self.assertIn("not connected", registration.status.last_error)
        self.assertFalse(registration.status.unsupported)

    async def test_old_unity_package_stops_renewals(self):
        call = AsyncMock(return_value="Error: Unhandled action 'register_event_listener'.")
        with patch("unity_bridge.call_unity", call), patch("asyncio.sleep", AsyncMock()):
            await registration.registration_loop(interval=0)
        self.assertTrue(registration.status.unsupported)
        self.assertEqual(call.await_count, 1)


class ReceivingProblemTests(unittest.TestCase):
    def setUp(self):
        registration.status = registration.RegistrationStatus()

    def tearDown(self):
        event_server.stop_event_server()
        registration.status = registration.RegistrationStatus()

    def test_primary_server_has_no_problem(self):
        event_server.start_event_server(port=_free_port())
        self.assertIsNone(registration.receiving_problem())

    def test_stopped_server_is_a_problem(self):
        self.assertIn("not running", registration.receiving_problem())

    def test_fallback_server_needs_recent_registration(self):
        with socket.socket() as other:
            other.bind(("127.0.0.1", 0))
            other.listen()
            event_server.start_event_server(port=other.getsockname()[1])
            registration.status.last_error = "Error: Unity Editor is not connected"
            problem = registration.receiving_problem(now=1000)
            self.assertIn(event_server.bound_url(), problem)
            self.assertIn("not connected", problem)
            registration.status.last_ok_at = 990
            self.assertIsNone(registration.receiving_problem(now=1000))
            self.assertIsNotNone(registration.receiving_problem(now=990 + registration.TTL_SECONDS + 1))


class RecentEventsToolTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        server = FastMCP("test")
        mcp_tools.register_unity_tools(server)
        self.tool = await server.get_tool("get_recent_unity_events")

    async def test_empty_window_explains_why_events_may_be_missing(self):
        with patch("events.events_buffer.get_recent_events", return_value=[]), \
                patch.object(registration, "receiving_problem", return_value="registration failed (x)."):
            result = await self.tool.fn(since_seconds_ago=60, limit=5)
        self.assertIn("No Unity events recorded", result)
        self.assertIn("registration failed (x).", result)

    async def test_empty_window_without_problem_keeps_plain_message(self):
        with patch("events.events_buffer.get_recent_events", return_value=[]), \
                patch.object(registration, "receiving_problem", return_value=None):
            result = await self.tool.fn(since_seconds_ago=60, limit=5)
        self.assertEqual(result, "No Unity events recorded in that window.")


if __name__ == "__main__":
    unittest.main()
