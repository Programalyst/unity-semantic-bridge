import collections
import threading
import time
from typing import Any, Optional

_log = collections.deque(maxlen=200)  # bounded — oldest silently evicted
_lock = threading.Lock()

def record_event(method: str, params: Any) -> None:
    with _lock:
        _log.append({"method": method, "params": params, "timestamp": time.time()})

def get_recent_events(since: Optional[float] = None, limit: int = 50) -> list[dict]:
    with _lock:
        events = list(_log)
    if since is not None:
        events = [e for e in events if e["timestamp"] > since]
    return events[-limit:]