# Define the function declarations

clickUiButton = {
  "name": "click_ui_button",
  "description": "Click a button with the specified button name.",
  "parameters": {
    "type": "object",
    "properties": {
      "ButtonName": {
        "type": "string",
        "description": "Name of the button to press",
      },
      "AncestorName": {
        "type": "string",
        "description": "Optional parameter for checking if the button has an ancenstor (a parent object) with the provided name. Used where there may be more than one button with the same buttonName.",
      },
      "Intent": {
        "type": "string",
        "description": "Specify the intent of the button press."
      },
    },
    "required": ["ButtonName", "Intent"]
  }
}

clickScreenPosition = {
  "name": "click_screen_position",
  "description": "Clicks the provided screen position in pixels. 0x, 0y is the top left of the screen.",
  "parameters": {
    "type": "object",
    "properties": {
      "screenX": {
        "type": "number",
        "description": "A float value number of pixels from the left to click. 0 would be left edge of the screen.",
      },
      "screenY": {
        "type": "number",
        "description": "A float value number of pixels from the top to click. 0 would be top edge of the screen.",
      },
      "Intent": {
        "type": "string",
        "description": "Specify what the click is intended to do.",
      },
    },
    "required": ["screenX", "screenY", "Intent"]
  }
}