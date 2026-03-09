import asyncio
from pathlib import Path
from websockets import ClientConnection
from dataclasses import dataclass

@dataclass
class AppState:
    # Claude / other agents may launch MCP server from a different path
    base_dir: Path = Path(__file__).parent.resolve()

    # Global reference to the socket instance with Unity
    unity_ws: ClientConnection = None 

    # Future for handling Editor time responses
    unity_res_future: asyncio.Future = None

    # Future for handling gameplay responses
    gameplay_future: asyncio.Future = None 

app_state = AppState()