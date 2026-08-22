import logging
from . import event_server
from . import events_buffer

# wire up Unity side for new handlers in EditorBridge.InstallEventHooks()
def register_event_handlers() -> None:
    """Wire Unity Editor push events (hierarchy/selection/play-mode/console/objectChanged)
    into the shared events_buffer, so get_recent_unity_events can read them
    back on demand. Call once, before event_server.start_event_server()."""

    @event_server.on_event("unity/hierarchyChanged")
    def _on_hierarchy_changed(params):
        logging.info(f"[event] hierarchyChanged: {params}")
        events_buffer.record_event("unity/hierarchyChanged", params)
        return "ack"

    @event_server.on_event("unity/selectionChanged")
    def _on_selection_changed(params):
        logging.info(f"[event] selectionChanged: {params}")
        events_buffer.record_event("unity/selectionChanged", params)
        return "ack"

    @event_server.on_event("unity/playModeStateChanged")
    def _on_playmode_changed(params):
        logging.info(f"[event] playModeStateChanged: {params}")
        events_buffer.record_event("unity/playModeStateChanged", params)
        return "ack"

    @event_server.on_event("unity/consoleLog")
    def _on_console_log(params):
        logging.info(f"[event] consoleLog: {params}")
        events_buffer.record_event("unity/consoleLog", params)
        return "ack"

    @event_server.on_event("unity/objectChanged")
    def _on_object_changed(params):
        logging.info(f"[event] objectChanged: {params}")
        events_buffer.record_event("unity/objectChanged", params)
        return "ack"