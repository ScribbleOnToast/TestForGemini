import socket
import os
import socket
import json
import os
import cv2
import numpy as np
import time
import signal
import sys
import queue
import multiprocessing as mp
import re 
import json
import ollama

# Configuration
SOCKET_PATH = "/tmp/digitaleye_brain.sock"
MODEL_NAME = "gemma3:1b"

SYSTEM_PROMPT = (
            "You are the brain of a wearable device. The user has spoken a command. You should analyze the intent of the users command and map it as defined below."            
            "Valid Intents:"
            "IDENTIFY: User wants to see/read/describe the environment (e.g. \"read text\", \"what is this\", \"describe scene\")."
            "SYSTEM: User wants to change or hear system settings (e.g. \"volume up\", \"shutdown\", \"battery\")."
            "OVERRIDE: User wants to control media playback flow (e.g. \"stop\", \"pause\", \"skip\")."      
            "ERROR: If the input is not understood or cannot be mapped to a valid intent."
            "Valid Payload examples:"
            "For SYSTEM: \"volume_up\", \"volume_down\", \"volume_set <number>\", \"mute\", \"unmute\", \"shutdown\"."
            "For IDENTIFY: Respond with the input payload. This will be passed to another recognizer."
            "For OVERRIDE: \"stop\", \"pause\", \"skip\", \"play\"."
            "For ERROR: \"I didn't understand that command.\""

            "Example Input: \"Make it louder\""
            "Example JSON: { \"intent\": \"SYSTEM\", \"payload\": \"volume_up\" }"
            
            "Example Input: \"Read this sign\""
            "Example JSON: { \"intent\": \"IDENTIFY\", \"payload\": \"Read this sign\" }"
)

def handle_intent(text):
    try:
        response = ollama.generate(
            model=MODEL_NAME,
            system=SYSTEM_PROMPT,
            prompt=f"Parse this: {text}",
            stream=False,
            options={"temperature": 0}, # Keep it deterministic
            format="json"
        )
        return response['response'].strip()
    except Exception as e:
        return json.dumps({"error": str(e)})

def start_server():
    print("Starting LLM Interface...")
    # Clean up the socket if it already exists
    if os.path.exists(SOCKET_PATH):
        os.remove(SOCKET_PATH)

    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server.bind(SOCKET_PATH)
    server.listen(1)
    print(f"Brain Engine listening on {SOCKET_PATH}...")
    conn, _ = server.accept()
    while True:
        conn, _ = server.accept()
        print("Client connected to Brain Engine")
        try:
            data = conn.recv(1024)
            if not data: continue  
            print(f"Received command: {data.decode('utf-8')}")
            user_text = data.decode('utf-8')
            result = handle_intent(user_text)
            conn.sendall(result.encode('utf-8'))
        except Exception as e:
            print(f"Error handling command: {e}")
    print("Shutting down Brain Engine...")
if __name__ == "__main__":
    start_server()