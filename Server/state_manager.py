import asyncio
from pathlib import Path
from websockets import ClientConnection
from dataclasses import dataclass, field
from typing import Optional, Any

@dataclass
class AppState:
    # Claude / other agents may launch MCP server from a different path
    base_dir: Path = Path(__file__).parent.resolve()

    # Global reference to the socket instance with Unity
    unity_ws: ClientConnection = None 

    # Orchestrator Agent (e.g. Claude) may kick of multiple MCP tool requests
    # use a dict for holding multiple futures for responses back from Unity
    # key: str is the uuid of the request
    # dataclass doesn't allow mutable objects (like a dict or list) to be used as default values directly
    # default_factory=dict tells the dataclass to create a brand-new dictionary every time a new AppState is initialized
    pending_requests: dict[str, asyncio.Future] = field(default_factory=dict)

    unity_request_lock = asyncio.Lock()

    # --- MCP client capability gating ---
    # Stores the last InitializeRequestParams received from the MCP client.
    # Used for upfront feature-gating of vision tools (screenshot analysis).
    client_capabilities: Optional[Any] = None
    client_info: Optional[Any] = None
    client_protocol_version: Optional[str] = None
    initialize_params: Optional[Any] = None

    # --- Injected LLM for gameplay/RunnableConfig ---
    # The user's LLM can be injected here via config["configurable"]["llm"]
    # and will be used by Runtime/image_analysis and LightingAgent
    llm_config: Optional[Any] = None
    gameplay_llm_config: Optional[Any] = None

app_state = AppState()