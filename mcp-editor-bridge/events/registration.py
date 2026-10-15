"""Keeps this server registered as a Unity event listener.

Several MCP servers can attach to one Editor (one per agent session), but only one can
bind the project's event port. Every server therefore registers the URL it actually
listens on with ``register_event_listener``; Unity sends each event to the event port
plus every live registration, so all of them see the Editor's events. Registrations
expire, so they are renewed periodically (and re-sent after Unity restarts).
"""
from __future__ import annotations

import asyncio
import logging
import time
from dataclasses import dataclass
from typing import Optional

from events import event_server
import unity_bridge

logger = logging.getLogger(__name__)

RENEW_INTERVAL_SECONDS = 30.0
TTL_SECONDS = 120.0


@dataclass
class RegistrationStatus:
    last_ok_at: Optional[float] = None
    last_error: Optional[str] = None
    # Set when the Unity package predates registration; renewals stop.
    unsupported: bool = False


status = RegistrationStatus()


async def register_once() -> bool:
    url = event_server.bound_url()
    if url is None:
        status.last_error = "event server is not running"
        return False
    result = await unity_bridge.call_unity(
        "register_event_listener", {"url": url, "ttlSeconds": TTL_SECONDS},
        timeout=5.0, retry_connect=False)
    if result.startswith("Error:"):
        status.last_error = result
        if "Unhandled action" in result:
            status.unsupported = True
            logger.warning("[registration] Unity bridge package predates event registration; "
                           "only the server bound to the event port receives events.")
        return False
    status.last_ok_at = time.time()
    status.last_error = None
    return True


async def registration_loop(interval: float = RENEW_INTERVAL_SECONDS) -> None:
    while not status.unsupported:
        try:
            await register_once()
        except Exception as e:  # keep renewing whatever happens to one attempt
            status.last_error = f"Error: {e}"
        await asyncio.sleep(interval)


def receiving_problem(now: Optional[float] = None) -> Optional[str]:
    """Why this server may be missing events, or None when it should be receiving them."""
    url = event_server.bound_url()
    if url is None:
        return "this MCP server's event server is not running, so it cannot receive Unity events."
    if event_server.is_primary():
        return None
    now = time.time() if now is None else now
    if status.last_ok_at is not None and now - status.last_ok_at < TTL_SECONDS:
        return None
    reason = status.last_error or "registration has not completed yet"
    return (f"the event port is used by another MCP server, so this server listens at {url} and needs "
            f"Unity to register it, which has not succeeded ({reason}).")
