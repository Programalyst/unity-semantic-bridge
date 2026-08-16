from langchain_core.runnables import RunnableConfig
from langchain_core.language_models import BaseChatModel

def get_llm_from_config(config: RunnableConfig | None) -> BaseChatModel:
        """Extract the caller's LLM from RunnableConfig.

        Expected shape: config["configurable"]["llm"] is a LangChain BaseChatModel.
        This allows the sub-agent to reuse the user's connected LLM.
        """
        if config is None:
            raise ValueError(
                "No LLM provided via RunnableConfig. "
                "Pass the user's LLM as `config={'configurable': {'llm': your_chat_model}}` "
                "when invoking the graph or diagnose_lighting_issue()."
            )
        
        # RunnableConfig is a dict-like with optional "configurable" key
        # Coerces both dicts and object-based RunnableConfigs safely
        configurable = (
            config.get("configurable") if isinstance(config, dict) 
            else getattr(config, "configurable", None)
        )

        if not isinstance(configurable, dict):
            raise ValueError(
                "Invalid RunnableConfig: expected config['configurable']['llm'] to be a BaseChatModel. "
                f"Got configurable={configurable!r}"
            )

        llm = configurable.get("llm")
        if llm is None:
            raise ValueError(
                "No LLM found in RunnableConfig. "
                "Provide it as `{'configurable': {'llm': your_chat_model}}`."
            )
        return llm