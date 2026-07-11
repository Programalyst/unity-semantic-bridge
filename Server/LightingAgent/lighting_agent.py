
from typing import Annotated, TypedDict, Literal
from langgraph.graph import StateGraph, END
from langgraph.graph.message import add_messages
from langgraph.prebuilt import ToolNode, tools_condition
from langchain_core.messages import HumanMessage, AIMessage, SystemMessage, ToolMessage
from langchain_google_genai import ChatGoogleGenerativeAI
import textwrap
from unity_bridge import fetch_screenshot_base64

import logging
logger = logging.getLogger(__name__)

# Define the agent state
class AgentState(TypedDict):
    messages: Annotated[list, add_messages] # use reducer to preserve checkpointing, allow parallel paths and update old messages by id
    iteration_count: int
    max_iterations: int
    issue_resolved: bool
    instance_id: int
    consecutive_tool_errors: int


class LightingDiagnosticAgent:
    """
    A LangGraph-based agent that iteratively diagnoses lighting issues in Unity.
    It will keep trying different diagnostic approaches until the issue is resolved or max iterations reached.
    """

    def __init__(self, unity_tools: dict, max_iterations: int = 5):
        """
        Args:
            unity_tools: Dictionary of Unity MCP tools (get_lights_affecting_object, get_urp_pipeline_settings, etc.)
            max_iterations: Maximum number of diagnostic iterations before giving up
        """
        
        self.unity_tools = unity_tools
        self.max_iterations = max_iterations
        self.llm = ChatGoogleGenerativeAI(model="gemini-2.5-flash", temperature=0)
        self.graph = self._build_graph()

        llm_with_tools = self.llm.bind_tools(list(self.unity_tools.values()))
        print(llm_with_tools.kwargs)

    def _build_graph(self):
        """Build the LangGraph workflow"""
        workflow = StateGraph(AgentState)

        # Define nodes
        workflow.add_node("diagnose", self._diagnose_node)
        workflow.add_node("check_resolution", self._check_resolution_node)
        workflow.add_node("tools", ToolNode(list(self.unity_tools.values())))

        # Define edges
        workflow.set_entry_point("diagnose")
        workflow.add_conditional_edges( # conditional check if there is no tool call
            "diagnose", 
            tools_condition,
            {
                "tools": "tools",
                END: "check_resolution"
            }
        )
        workflow.add_edge("tools", "check_resolution")
        workflow.add_conditional_edges(
            "check_resolution",
            self._should_continue,
            {
                "continue": "diagnose",
                "end": END
            }
        )

        return workflow.compile()

    def _diagnose_node(self, state: AgentState) -> AgentState:
        """Agent decides what diagnostic action to take next"""
        messages = state["messages"]
        iteration = state["iteration_count"]

        logger.info(f"Diagnostic iteration {iteration}/{state['max_iterations']}")

        for i, m in enumerate(messages):
            logger.info(f"  msg[{i}] type={type(m).__name__} content={m.content!r} tool_calls={getattr(m, 'tool_calls', None)}")

        # Bind tools to LLM
        llm_with_tools = self.llm.bind_tools(list(self.unity_tools.values()))

        # Get response from LLM
        response = llm_with_tools.invoke(messages)

        return {"messages": [response]}

    def _check_resolution_node(self, state: AgentState) -> AgentState:
        """Check if the lighting issue has been resolved"""
        messages = state["messages"]
        iteration = state["iteration_count"] + 1

        # Track repeated tool failures to allow early abort
        consecutive_errors = state.get("consecutive_tool_errors", 0)
        last_msg = messages[-1] if messages else None
        if isinstance(last_msg, ToolMessage) and "error" in str(last_msg.content).lower():
            consecutive_errors += 1
        else:
            consecutive_errors = 0

        # Ask LLM if issue is resolved based on diagnostic results
        check_prompt = """
        Based on the diagnostic information gathered so far, has the lighting issue been identified and resolved?
        Respond with ONLY 'RESOLVED' if the issue is fixed or fully diagnosed with a clear solution.
        Respond with ONLY 'CONTINUE' if more investigation is needed.
        """

        response = self.llm.invoke(messages + [HumanMessage(content=check_prompt)])
        is_resolved = "RESOLVED" in response.content.upper()

        logger.info(f"Resolution check: {'RESOLVED' if is_resolved else 'CONTINUE'}")

        return {
            "iteration_count": iteration,
            "issue_resolved": is_resolved,
            "consecutive_tool_errors": consecutive_errors
        }

    def _should_continue(self, state: AgentState) -> Literal["continue", "end"]:
        """Decide whether to continue diagnosing or end"""
        if state["issue_resolved"]:
            return "end"
        
        if state.get("consecutive_tool_errors", 0) >= 2:
            logger.warning("Aborting: 2 consecutive tool errors")
            return "end"

        if state["iteration_count"] >= state["max_iterations"]:
            logger.warning(f"Max iterations ({state['max_iterations']}) reached without resolution")
            return "end"

        return "continue"

    async def diagnose_lighting_issue(self, instance_id: int, issue_description: str) -> str:
        """
        Main entry point to diagnose a lighting issue on a GameObject.

        Args:
            instance_id: Unity GameObject instance ID
            issue_description: Description of the lighting problem

        Returns:
            Final diagnostic report
        """
        system_prompt = textwrap.dedent(f"""\
            You are a Unity lighting diagnostic expert. Your goal is to diagnose and resolve lighting issues.

            Current task: Diagnose the lighting issue for GameObject with instance_id={instance_id}
            Issue description: {issue_description}

            Follow this diagnostic process:
            1. Check what lights are affecting the object.
            2. Check URP pipeline settings for rendering configuration. Pay attention not just to binary
            on/off settings, but to any numeric limits or caps (e.g. per-object light limits, shadow
            distance, cascade counts) — these can cause partial or inconsistent lighting that's easy
            to miss if you only check whether a setting is "enabled."
            3. If a numeric limit is relevant, check how it compares against the actual conditions in the
            scene (e.g. how many lights are near the object vs. the per-object limit) rather than just
            noting the limit's existence.
            4. Inspect the object's Renderer component (material, rendering layer, etc.), including Culling
            Mask and Rendering Layer Mask compatibility with the light.
            5. Based on findings, identify the root cause — prefer explanations that are consistent with all
            reported symptoms (e.g. if the issue is inconsistent or angle/position-dependent rather than
            a complete absence of light, favor causes that would produce that specific pattern).
            6. Provide clear recommendations to fix the issue.

            Keep iterating through diagnostics until you find the root cause or have exhausted all possibilities.""")
        
        screenshot_b64 = await fetch_screenshot_base64()

        initial_state: AgentState = {
            "messages": [
                SystemMessage(content=system_prompt),
                HumanMessage(content=[
                    {"type": "text", "text": f"Begin diagnosis for GameObject instance_id={instance_id}"},
                    {"type": "image_url", "image_url": f"data:image/jpeg;base64,{screenshot_b64}"},
                ])
            ],
            "iteration_count": 0,
            "max_iterations": self.max_iterations,
            "consecutive_tool_errors": 0,
            "issue_resolved": False,
            "instance_id": instance_id
        }

        # helper method to handle Gemini (langchain_google_genai) returning a list of content blocks instead of a string
        def _content_to_str(content) -> str:
            if isinstance(content, str):
                return content
            if isinstance(content, list):
                parts = []
                for item in content:
                    if isinstance(item, str):
                        parts.append(item)
                    elif isinstance(item, dict) and "text" in item:
                        parts.append(item["text"])
                return "\n".join(parts)
            return str(content)

        try:
            final_state = await self.graph.ainvoke(initial_state)

            # Extract final report from messages
            report_parts = []
            for msg in final_state["messages"]:
                if isinstance(msg, AIMessage):
                    text = _content_to_str(msg.content)
                    if text.strip(): # skip empty/tool-call-only messages
                        report_parts.append(text)

            final_report = "\n\n".join(report_parts)

            if final_state["issue_resolved"]:
                return f"✅ Lighting issue diagnosed successfully!\n\n{final_report}"
            else:
                return f"⚠️ Diagnostic completed ({final_state['iteration_count']} iterations) but issue may need manual review.\n\n{final_report}"

        except Exception as e:
            logger.error(f"Error during lighting diagnosis: {e}", exc_info=True)
            return f"Error during diagnosis: {str(e)}"
