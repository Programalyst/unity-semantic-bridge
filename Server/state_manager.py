import asyncio
from pathlib import Path
from websockets import ClientConnection
from dataclasses import dataclass

@dataclass
class AppState:
    # Claude / other agents may launch MCP server from a different path
    base_dir: Path = Path(__file__).parent.resolve()
    # Global reference to the active Unity MPE socket
    unity_ws: ClientConnection = None 
    unity_res_future: asyncio.Future = None

app_state = AppState()