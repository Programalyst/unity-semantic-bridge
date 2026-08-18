import asyncio
from pathlib import Path
from dataclasses import dataclass, field
from typing import Optional, Any

@dataclass
class AppState:
    # Claude / other agents may launch MCP server from a different path
    base_dir: Path = Path(__file__).parent.resolve()

    # HTTP bridge to Unity Editor (JSON-RPC over HTTP)
    unity_base_url: str = "http://127.0.0.1:1073"
    unity_rpc_path: str = "/rpc"

    # Python JSON-RPC event server (Unity -> Python notifications)
    python_event_host: str = "127.0.0.1"
    python_event_port: int = 1074
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

app_state = AppState()
