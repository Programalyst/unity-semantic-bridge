using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// Thread-safe queue for HTTP requests received on background threads
    /// that need to be processed on Unity's main thread. Drains one message
    /// per EditorApplication.update tick.
    /// </summary>
    public class MainThreadMessageQueue
    {
        public class QueuedMessage
        {
            public string Json;
            public TaskCompletionSource<string> Completion;
        }

        private readonly Queue<QueuedMessage> _pending = new();
        private readonly object _lock = new();
        private Action<QueuedMessage> _onMessage;

        public void Start(Action<QueuedMessage> onMessage)
        {
            _onMessage = onMessage;
            EditorApplication.update -= Drain;
            EditorApplication.update += Drain;
        }

        public void Stop()
        {
            EditorApplication.update -= Drain;
        }

        /// <summary>Thread-safe enqueue — safe to call from HTTP listener thread.</summary>
        public void Enqueue(string json, TaskCompletionSource<string> completion)
        {
            lock (_lock)
            {
                _pending.Enqueue(new QueuedMessage { Json = json, Completion = completion });
            }
        }

        private void Drain()
        {
            QueuedMessage message = null;
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
