from google import genai
from google.genai import types
from dotenv import load_dotenv

import os
import json

load_dotenv() # Load environment variables from .env file

gemini_sdk_client = genai.Client(api_key=os.getenv("GEMINI_API_KEY"))

config = types.GenerateContentConfig(
    temperature=1.0,
)

async def gemini_scene_analysis(user_query, scene_json):
    # Flatten the scene for the prompt
    context = json.dumps(scene_json, indent=2)
    
    prompt = f"""
    You are a Senior Unity Developer Assistant. 
    A developer is asking you a question about their current scene.
    
    Current Scene Hierarchy (JSON):
    {context}
    
    User Question: {user_query}

    ### IMPORTANT INSTRUCTIONS:
    - Use Unity-compatible Rich Text tags ONLY.
    - Use <b>text</b> for bold (instead of **).
    - Use <i>text</i> for italics (instead of *).
    - Use <color=#00ff00>text</color> for success/positive info.
    - Use <color=#ff0000>text</color> for warnings/errors.
    - Use <size=14><b>Header Text</b></size> for section titles.
    - DO NOT use Markdown (no #, no \`\`\`, no **).
    - Use standard newlines (\n) for spacing.
    
    Provide a concise, helpful answer based ONLY on the provided scene data. 
    If you see issues with the hierarchy or missing components, point them out.
    """

    response = gemini_sdk_client.models.generate_content(
        model="gemini-2.5-flash",
        contents=[prompt],
        config=config
        # No tool config needed for chat mode
    )
    print(f"Tokens used: {response.usage_metadata.total_token_count}")
    
    return response.text if response.text else "I couldn't analyze the scene."