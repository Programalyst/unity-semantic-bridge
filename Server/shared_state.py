import asyncio

class SharedState:
    # Use a class-level variable so it's a singleton
    unity_res_future: asyncio.Future = None