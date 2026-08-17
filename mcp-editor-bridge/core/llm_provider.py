# core/llm_provider.py — server-owned, not client-borrowed
# Sampling was formally deprecated in MCP spec revision 2026-07-28, under SEP-2577.
# This module replaces the old RunnableConfig / client-sampling approach with a
# direct provider call owned by the server.
import os
from langchain.chat_models import init_chat_model
from langchain_core.language_models import BaseChatModel


def get_diagnostic_llm(
    provider: str | None = None,
    model: str | None = None,
) -> BaseChatModel:
    """
    Returns a server-owned BaseChatModel for the lighting diagnostic sub-agent.

    Provider/model default to LIGHTING_LLM_PROVIDER / LIGHTING_LLM_MODEL env vars
    (falling back to "anthropic" / "claude-sonnet-4-0"). The provider's own
    credential env vars (e.g. ANTHROPIC_API_KEY, OPENAI_API_KEY) must be set —
    no client sampling or API key forwarding is used.
    """
    provider = provider or os.environ.get("LIGHTING_LLM_PROVIDER", "anthropic")
    model = model or os.environ.get("LIGHTING_LLM_MODEL", "claude-sonnet-4-0")
    try:
        return init_chat_model(model=model, model_provider=provider, temperature=0.0)
    except Exception as exc:
        raise RuntimeError(
            f"Error: Could not initialize LLM provider='{provider}' model='{model}'. "
            f"Check LIGHTING_LLM_PROVIDER/LIGHTING_LLM_MODEL and the provider's "
            f"required credentials/env vars are set. Underlying error: {exc}"
        ) from exc
