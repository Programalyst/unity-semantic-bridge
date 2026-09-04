"""LangGraph workflow for diagnosing Unity lighting via server-owned LLM.

Sampling was deprecated in MCP spec revision 2026-07-28 (SEP-2577); this module
uses get_diagnostic_llm() (core/llm_provider) directly and executes tools
explicitly — no ctx.sample_step / execute_tools delegation.
"""

from __future__ import annotations

import base64
import logging
import textwrap
from typing import Any, Literal, TypedDict

from langgraph.graph import END, StateGraph
from langchain_core.messages import AIMessage, BaseMessage, HumanMessage, SystemMessage, ToolMessage
from langchain_core.tools import BaseTool, StructuredTool, tool as lc_tool

from core.llm_provider import get_diagnostic_llm

logger = logging.getLogger(__name__)


class AgentState(TypedDict):
    messages: list[BaseMessage]
    iteration_count: int
    max_iterations: int
    final_report: str
    awaiting_final_report: bool


class LightingDiagnosticAgent:
    """A bounded LangGraph workflow backed by a server-owned LLM."""

    def __init__(self, unity_tools: dict[str, object], max_iterations: int = 5):
        self.max_iterations = max_iterations
        # Normalize every callable to a proper LangChain BaseTool so bind_tools works
        # for both sync and async functions. FastMCP's @mcp.tool() returns plain
        # callables, not BaseTools.
        self._lc_tools: list[BaseTool] = []
        self.unity_tools: dict[str, BaseTool] = {}
        for name, fn in unity_tools.items():
            lc = self._to_lc_tool(fn, name)
            self.unity_tools[name] = lc
            self._lc_tools.append(lc)

    @staticmethod
    def _to_lc_tool(fn: object, name: str) -> BaseTool:
        if isinstance(fn, BaseTool):
            return fn
        # Prefer the @tool decorator path which preserves Annotated signatures
        # and async support. Fall back to StructuredTool.from_function.
        try:
            # lc_tool is a decorator factory; calling it on fn returns a tool
            wrapped = lc_tool(fn)  # type: ignore[arg-type]
            if isinstance(wrapped, BaseTool):
                # Ensure name matches the key the agent expects
                if wrapped.name != name:
                    wrapped.name = name  # type: ignore[attr-defined]
                return wrapped
        except Exception:
            pass
        try:
            return StructuredTool.from_function(
                func=fn,  # type: ignore[arg-type]
                name=name,
                description=getattr(fn, "__doc__", None) or f"Unity tool {name}",
                infer_schema=True,
            )
        except Exception as exc:
            logger.warning(f"Falling back to generic wrapper for tool {name}: {exc}")
            # Last resort: create a simple async wrapper
            async def _generic(**kwargs: Any) -> str:
                if callable(fn):
                    res = fn(**kwargs)  # type: ignore[call-arg]
                    if hasattr(res, "__await__"):
                        res = await res  # type: ignore[no-redef]
                    return str(res)
                return f"Error: tool {name} is not callable"

            _generic.__name__ = name
            _generic.__doc__ = getattr(fn, "__doc__", f"Unity tool {name}")
            return StructuredTool.from_function(func=_generic, name=name, description=_generic.__doc__ or name)  # type: ignore[arg-type]

    @staticmethod
    def _system_prompt(instance_id: int, issue_description: str) -> str:
        return textwrap.dedent(f"""\
            You are a Unity URP lighting diagnostic expert.

            Diagnose the reported issue on GameObject instance_id={instance_id}.
            Reported issue: {issue_description}

            Use the provided Unity diagnostic tools to gather evidence before drawing
            conclusions. Check nearby lights, URP configuration, and the affected
            object's renderer/material settings as appropriate. Do not modify the
            scene. When you have enough evidence, stop calling tools and return a
            concise final report containing: root cause, evidence, and recommended
            fixes. If evidence is insufficient, say what requires manual review.
        """)

    def _build_graph(self, system_prompt: str):
        workflow = StateGraph(AgentState)

        async def diagnose_node(state: AgentState) -> dict[str, Any]:
            # Fail-fast is handled in diagnose_lighting_issue before graph entry,
            # but also guard here in case LLM was reconfigured mid-run.
            try:
                llm = get_diagnostic_llm()
            except Exception as exc:
                logger.exception("Failed to initialize diagnostic LLM")
                return {"final_report": f"Error: {exc}", "awaiting_final_report": False}

            # Bind tools and invoke with full history (system prompt prepended)
            try:
                llm_with_tools = llm.bind_tools(self._lc_tools)
                # Prepend system prompt as SystemMessage for this invocation
                invoke_messages: list[BaseMessage] = [SystemMessage(content=system_prompt)] + state["messages"]
                response: AIMessage = await llm_with_tools.ainvoke(invoke_messages)  # type: ignore[assignment]
            except Exception as exc:
                logger.exception("LLM invocation failed")
                return {"final_report": f"Error: LLM invocation failed: {exc}", "awaiting_final_report": False}

            # No tool calls -> final report
            tool_calls = getattr(response, "tool_calls", None)
            if not tool_calls:
                text = response.content if isinstance(response.content, str) else str(response.content)
                return {
                    "messages": [response],
                    "final_report": text.strip() or "The model returned an empty diagnostic report.",
                    "awaiting_final_report": False,
                }

            # Execute each tool call explicitly (MCP sampling's execute_tools is gone)
            follow_ups: list[BaseMessage] = [response]
            for tc in tool_calls:
                if isinstance(tc, dict):
                    name = tc.get("name", "")
                    args = tc.get("args", {}) or {}
                    call_id = tc.get("id", "")
                else:
                    name = getattr(tc, "name", "")
                    args = getattr(tc, "args", {}) or {}
                    call_id = getattr(tc, "id", "") or getattr(tc, "tool_call_id", "")

                tool = self.unity_tools.get(name)
                if tool is None:
                    follow_ups.append(
                        ToolMessage(content=f"Error: unknown tool '{name}'", tool_call_id=call_id, name=name)
                    )
                    continue

                try:
                    # BaseTool.ainvoke handles async and sync transparently
                    result = await tool.ainvoke(args)  # type: ignore[arg-type]
                except Exception as exc:
                    logger.exception(f"Tool {name} failed")
                    follow_ups.append(
                        ToolMessage(content=f"Error: tool '{name}' failed: {exc}", tool_call_id=call_id, name=name)
                    )
                    continue

                # Vision plumbing: get_screenshot returns fastmcp.utilities.types.Image
                # (data: bytes, format: str). LangChain ToolMessages are text-only by
                # default, so we split into a ToolMessage + follow-up HumanMessage with
                # an image_url block that vision models can actually see.
                try:
                    from fastmcp.utilities.types import Image as FastImage

                    if isinstance(result, FastImage):
                        b64 = base64.b64encode(result.data).decode()
                        fmt = getattr(result, "_format", None) or getattr(result, "format", "jpeg")
                        follow_ups.append(
                            ToolMessage(
                                content=f"Screenshot captured ({len(result.data)} bytes, format={fmt}) — see following image.",
                                tool_call_id=call_id,
                                name=name,
                            )
                        )
                        follow_ups.append(
                            HumanMessage(
                                content=[
                                    {"type": "text", "text": f"Result of {name} (image below):"},
                                    {"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{b64}"}},
                                ]
                            )
                        )
                        continue
                except ImportError:
                    pass

                # Generic result -> string ToolMessage
                if isinstance(result, (bytes, bytearray)):
                    # Fallback for unexpected bytes
                    b64 = base64.b64encode(result).decode()
                    follow_ups.append(
                        ToolMessage(content=b64, tool_call_id=call_id, name=name)
                    )
                elif isinstance(result, str):
                    follow_ups.append(ToolMessage(content=result, tool_call_id=call_id, name=name))
                else:
                    follow_ups.append(ToolMessage(content=str(result), tool_call_id=call_id, name=name))

            return {
                "messages": follow_ups,
                "iteration_count": state["iteration_count"] + 1,
                "awaiting_final_report": True,
            }

        async def summarize_node(state: AgentState) -> dict[str, Any]:
            # Iteration budget exhausted but evidence was gathered: ask the LLM
            # once, WITHOUT tools, to write the final report from history.
            try:
                llm = get_diagnostic_llm()
            except Exception as exc:
                logger.exception("Failed to initialize diagnostic LLM")
                return {"final_report": f"Error: {exc}", "awaiting_final_report": False}
            try:
                closing = (
                    system_prompt
                    + "\n\nThe tool-round budget is exhausted. Do not call any tools. "
                      "Write the final diagnostic report now from the evidence gathered, "
                      "containing: root cause, evidence, and recommended fixes. "
                      "If evidence is insufficient, say what requires manual review."
                )
                invoke_messages: list[BaseMessage] = [SystemMessage(content=closing)] + state["messages"]
                response: AIMessage = await llm.ainvoke(invoke_messages)  # type: ignore[assignment]
                text = response.content if isinstance(response.content, str) else str(response.content)
                return {
                    "messages": [response],
                    "final_report": text.strip() or "The model returned an empty diagnostic report.",
                    "awaiting_final_report": False,
                }
            except Exception as exc:
                logger.exception("Final summarization failed")
                return {"final_report": f"Error: LLM invocation failed: {exc}", "awaiting_final_report": False}

        def next_node(state: AgentState) -> Literal["continue", "summarize", "end"]:
            if not state["awaiting_final_report"]:
                return "end"
            if state["iteration_count"] >= state["max_iterations"]:
                return "summarize"
            return "continue"

        workflow.add_node("diagnose", diagnose_node)
        workflow.add_node("summarize", summarize_node)
        workflow.set_entry_point("diagnose")
        workflow.add_conditional_edges(
            "diagnose", next_node, {"continue": "diagnose", "summarize": "summarize", "end": END}
        )
        workflow.add_edge("summarize", END)
        return workflow.compile()

    async def diagnose_lighting_issue(
        self,
        instance_id: int,
        issue_description: str,
    ) -> str:
        """Run the diagnostic workflow using the server-owned LLM."""
        # Fail-fast on missing/misconfigured provider before building graph
        try:
            get_diagnostic_llm()
        except Exception as exc:
            return f"Error: {exc}"

        system_prompt = self._system_prompt(instance_id, issue_description)
        initial_state: AgentState = {
            "messages": [
                HumanMessage(
                    content=(
                        f"Begin the diagnosis for GameObject instance_id={instance_id}. "
                        f"Reported issue: {issue_description} "
                        "Call get_screenshot with source='scene' and focus_instance_id if you need visual context."
                    )
                ),
            ],
            "iteration_count": 0,
            "max_iterations": self.max_iterations,
            "final_report": "",
            "awaiting_final_report": True,
        }

        graph = self._build_graph(system_prompt)
        final_state = await graph.ainvoke(initial_state)
        report = final_state["final_report"].strip()
        if report:
            return report

        return (
            f"Diagnostic stopped after {final_state['iteration_count']} tool rounds. "
            "The available evidence requires manual review."
        )
