import asyncio
from pathlib import Path
from dataclasses import dataclass, field
from typing import Optional, Any

@dataclass
class AppState:
    # Claude / other agents may launch MCP server from a different path
    base_dir: Path = Path(__file__).parent.resolve()

    # HTTP bridge to Unity Editor (replaces websocket)
    unity_base_url: str = "http://127.0.0.1:1073"

    # Serialize MCP→Unity calls — Unity handles one request at a time (_isProcessing)
    unity_request_lock: asyncio.Lock = field(default_factory=asyncio.Lock)

    # --- MCP client capability gating ---
    # Stores the last InitializeRequestParams received from the MCP client.
    # Used for upfront feature-gating of vision tools (screenshot analysis).
    client_capabilities: Optional[Any] = None
    client_info: Optional[Any] = None
    client_protocol_version: Optional[str] = None
    initialize_params: Optional[Any] = None

    # --- Injected LLM for RunnableConfig ---
    # The user's LLM can be injected here via config["configurable"]["llm"]
    # and will be used by LightingAgent
    llm_config: Optional[Any] = None

app_state = AppState()
