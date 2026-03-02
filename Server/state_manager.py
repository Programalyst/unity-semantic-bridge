import asyncio
from websockets import ClientConnection
from dataclasses import dataclass

@dataclass
class AppState:
    # Global reference to the active Unity MPE socket
    unity_ws: ClientConnection = None 
    unity_res_future: asyncio.Future = None

app_state = AppState()