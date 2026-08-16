Bridge posts to Unity's HTTP listener at `http://127.0.0.1:1073/mcp` (health: `GET /health`).

Lighting subagent uses the injected LLM via `RunnableConfig` (`config["configurable"]["llm"]`) and the vision capability gate. No separate API key is required for the bridge itself.
