using System;
using System.Collections.Generic;
using UnityEditor;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// Thread-safe queue for messages received on a background thread (e.g. websocket receive loop)
    /// that need to be processed on Unity's main thread. Drains one message per EditorApplication.update
    /// tick, since EditorApplication.update keeps running (even if throttled) when the Editor loses focus,
    /// unlike EditorApplication.delayCall which can stall for extended periods when unfocused.
    /// </summary>
    public class MainThreadMessageQueue
    {
        private readonly Queue<string> _pending = new();
        private readonly object _lock = new();
        private Action<string> _onMessage;

        public void Start(Action<string> onMessage)
        {
            _onMessage = onMessage;
            EditorApplication.update -= Drain;
            EditorApplication.update += Drain;
        }

        public void Stop()
        {
            EditorApplication.update -= Drain;
        }

        /// <summary>Thread-safe enqueue — safe to call from a background thread.</summary>
        public void Enqueue(string message)
        {
            lock (_lock)
            {
                _pending.Enqueue(message);
            }
        }

        // Only pull 1 message per tick — avoids overwhelming Unity by flushing
        // a large backlog all at once after a period of throttling.
        private void Drain()
        {
            string message = null;
            lock (_lock)
            {
                if (_pending.Count > 0)
                    message = _pending.Dequeue();
            }
            if (message != null)
                _onMessage?.Invoke(message);
        }
    }
}