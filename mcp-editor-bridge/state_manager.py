import argparse
import asyncio
from pathlib import Path
from dataclasses import dataclass, field
from typing import Optional, Any, Mapping, Sequence

DEFAULT_UNITY_PORT = 1073
DEFAULT_EVENT_PORT = 1074
MIN_PORT, MAX_PORT = 1024, 65535

@dataclass
class AppState:
    # Claude / other agents may launch MCP server from a different path
    base_dir: Path = Path(__file__).parent.resolve()

    # HTTP bridge to Unity Editor (JSON-RPC over HTTP); port set by configure_ports
    unity_base_url: str = f"http://127.0.0.1:{DEFAULT_UNITY_PORT}"
    unity_rpc_path: str = "/rpc"

    # Python JSON-RPC event server (Unity -> Python notifications)
    python_event_host: str = "127.0.0.1"
    python_event_port: int = DEFAULT_EVENT_PORT
    python_event_path: str = "/rpc"

    # Unity's HttpListener accepts concurrently, but the MainThreadMessageQueue drain only
    # ever processes one item at a time — this keeps request ordering
    # predictable and avoids concurrent access to last_reload_trigger_at
    unity_request_lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    last_reload_trigger_at: float = 0

    @property
    def unity_rpc_url(self) -> str:
        return f"{self.unity_base_url}{self.unity_rpc_path}"

    @property
    def python_event_url(self) -> str:
        return f"http://{self.python_event_host}:{self.python_event_port}{self.python_event_path}"

    def configure_ports(self, unity_port: int, event_port: int) -> None:
        self.unity_base_url = f"http://127.0.0.1:{unity_port}"
        self.python_event_port = event_port


def resolve_ports(argv: Sequence[str], env: Mapping[str, str]) -> tuple[int, int]:
    """Ports for this server: --unity-port/--event-port, else USB_UNITY_PORT/USB_EVENT_PORT, else 1073/1074.

    Each Unity project stores its own pair (Tools > Unity Semantic Bridge); the MCP
    server registered for that project must use the same pair. Raises ValueError.
    """
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--unity-port")
    parser.add_argument("--event-port")
    args, _ = parser.parse_known_args(list(argv))

    def pick(cli: Optional[str], env_name: str, default: int, label: str) -> int:
        raw = cli if cli is not None else env.get(env_name)
        if raw is None or raw == "":
            return default
        try:
            port = int(raw)
        except ValueError:
            raise ValueError(f"{label} must be an integer, got {raw!r}") from None
        if not MIN_PORT <= port <= MAX_PORT:
            raise ValueError(f"{label} must be between {MIN_PORT} and {MAX_PORT}, got {port}")
        return port

    unity_port = pick(args.unity_port, "USB_UNITY_PORT", DEFAULT_UNITY_PORT, "Unity port")
    event_port = pick(args.event_port, "USB_EVENT_PORT", DEFAULT_EVENT_PORT, "Event port")
    if unity_port == event_port:
        raise ValueError(f"Unity port and event port must differ (both {unity_port})")
    return unity_port, event_port


app_state = AppState()
