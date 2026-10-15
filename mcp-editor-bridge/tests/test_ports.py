"""Per-project port configuration: never contacts a running Unity Editor."""
import unittest

import unity_bridge
from state_manager import AppState, DEFAULT_EVENT_PORT, DEFAULT_UNITY_PORT, resolve_ports


class ResolvePortsTests(unittest.TestCase):
    def test_defaults_without_arguments_or_environment(self):
        self.assertEqual(resolve_ports([], {}), (DEFAULT_UNITY_PORT, DEFAULT_EVENT_PORT))

    def test_cli_arguments(self):
        self.assertEqual(resolve_ports(["--unity-port", "1075", "--event-port", "1076"], {}), (1075, 1076))
        self.assertEqual(resolve_ports(["--unity-port=1077"], {}), (1077, DEFAULT_EVENT_PORT))

    def test_environment_used_when_no_cli_argument(self):
        env = {"USB_UNITY_PORT": "1075", "USB_EVENT_PORT": "1076"}
        self.assertEqual(resolve_ports([], env), (1075, 1076))
        self.assertEqual(resolve_ports(["--unity-port", "1080"], env), (1080, 1076))

    def test_empty_environment_value_falls_back_to_default(self):
        self.assertEqual(resolve_ports([], {"USB_UNITY_PORT": ""}), (DEFAULT_UNITY_PORT, DEFAULT_EVENT_PORT))

    def test_unrelated_arguments_are_ignored(self):
        self.assertEqual(resolve_ports(["--verbose", "--unity-port", "1075"], {}), (1075, DEFAULT_EVENT_PORT))

    def test_invalid_values_are_rejected(self):
        for argv in (["--unity-port", "abc"], ["--unity-port", "80"], ["--event-port", "70000"],
                     ["--unity-port", "1075", "--event-port", "1075"]):
            with self.subTest(argv=argv), self.assertRaises(ValueError):
                resolve_ports(argv, {})


class ConfigurePortsTests(unittest.TestCase):
    def test_configure_ports_updates_both_urls(self):
        state = AppState()
        state.configure_ports(1075, 1076)
        self.assertEqual(state.unity_rpc_url, "http://127.0.0.1:1075/rpc")
        self.assertEqual(state.python_event_url, "http://127.0.0.1:1076/rpc")

    def test_not_connected_message_names_the_configured_url(self):
        state = unity_bridge.app_state
        saved = (state.unity_base_url, state.python_event_port)
        try:
            state.configure_ports(1075, 1076)
            message = unity_bridge._not_connected_msg()
            self.assertIn("http://127.0.0.1:1075/rpc", message)
            self.assertIn("not connected", message.lower())
        finally:
            state.unity_base_url, state.python_event_port = saved


if __name__ == "__main__":
    unittest.main()
