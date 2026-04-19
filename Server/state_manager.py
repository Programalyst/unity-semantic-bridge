import asyncio
from pathlib import Path
from websockets import ClientConnection
from dataclasses import dataclass, field

@dataclass
class AppState:
    # Claude / other agents may launch MCP server from a different path
    base_dir: Path = Path(__file__).parent.resolve()

    # Global reference to the socket instance with Unity
    unity_ws: ClientConnection = None 

    # dict for holding futures for responses back from Unity
    # str is the uuid of the request
    # dataclass doesn't allow mutable objects (like a dict or list) to be used as default values directly
    # default_factory=dict tells the dataclass to create a brand-new dictionary every time a new AppState is initialized
    pending_requests: dict[str, asyncio.Future] = field(default_factory=dict)

    # Future for handling gameplay responses
    gameplay_future: asyncio.Future = None 

app_state = AppState()