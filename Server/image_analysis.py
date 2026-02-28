from google import genai
from google.genai import types
from google.genai.types import FunctionCall  # Import the type explicitly
import os
from dotenv import load_dotenv
from toolDeclarations import clickScreenPosition, clickUiButton

from pathlib import Path

import base64
import json

load_dotenv() # Load environment variables from .env file

# Configure the client and tools
gemini_sdk_client = genai.Client(api_key=os.getenv("GEMINI_API_KEY"))
tools = types.Tool(function_declarations=[clickScreenPosition, clickUiButton])
tool_config = types.ToolConfig(
    function_calling_config=types.FunctionCallingConfig(
        # "ANY" forces the model to use a tool call, "AUTO" allows the model to decide TOOL or TEXT
        mode="ANY",
        # Optional: restrict it to ONLY these specific tools
        allowed_function_names=["click_screen_position", "click_ui_button"]
    )
)

system_prompt = Path("system_prompt.txt").read_text(encoding="utf-8")

config = types.GenerateContentConfig(
    temperature=0,
    automatic_function_calling=types.AutomaticFunctionCallingConfig(disable=False),
    tools=[tools],
    tool_config=tool_config,
    system_instruction=system_prompt
)

async def gemini_image_analysis(scene_json, b64_image) -> list[FunctionCall] | str:
    
    # 1. Prepare the Image Part
    image_bytes = base64.b64decode(b64_image)
    image_part = types.Part.from_bytes(data=image_bytes, mime_type="image/jpeg")

    # 2. Prepare the Semantic Context (JSON)
    # We turn the JSON object back into a string for the prompt
    semantic_context = json.dumps(scene_json, indent=2)

    # 3. Build the Master Prompt
    # This tells Gemini: "Look at the image, but use this JSON for exact coordinates"
    prompt = f"""
    You are an autonomous agent playing a Unity tactics game. 
    Attached is the current screenshot and the semantic scene data in JSON format.
    
    ### Semantic Scene Data:
    {semantic_context}
    
    ### Task:
    Analyze the image and the JSON. Identify the best tactical move. 
    Use the `viewportPos` and `path` from the JSON to identify targets.
    """

    # 4. Generate Content
    # Ensure 'config' includes your tools/function_declarations for clicking
    response = gemini_sdk_client.models.generate_content(
        model="gemini-2.5-flash",
        contents=[prompt,image_part],
        config=config,
    )
    print(f"Tokens used: {response.usage_metadata.total_token_count}")

    return response
    
