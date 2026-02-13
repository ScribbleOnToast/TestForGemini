import socket
import json
import os
import cv2
import numpy as np
import time
import signal
import sys
from picamera2 import Picamera2
from vision_engine import Backend

# Configuration
SOCKET_PATH = "/tmp/digitaleye_vision.sock"
HEF_PATH = "../model/vlm/Qwen2-VL-2B-Instruct.hef"
# The system prompt should tell it to be concise.
SYSTEM_PROMPT = "You are a visual guide for a blind user. You are being shown an image taken from a chest-mounted camera worn by the user. Help the user understand the environment they are in."

USER_PROMPT = "Describe the scene in front of me."

class DigitalEyeBridge:
    def __init__(self):
        print(f"[INTERFACE] DigitalEye Brain: Initializing 10H Hardware...")
        self.running = True
        try:
            # Initialize Backend (Background Worker)
            self.vision_engine = Backend(
                hef_path=str(HEF_PATH),
                max_tokens=150,
                temperature=0.1,
                seed=42,
                system_prompt=SYSTEM_PROMPT
            )
        except Exception as e:
            print(f"[INTERFACE] Failed to initialize backend: {e}")
            self.running = False
            return

        try:
            # Setup Camera 3 Wide
            self.picam2 = Picamera2()
            config = self.picam2.create_preview_configuration(
                sensor={"output_size": (2304, 1296)},
                main={"size": (1536, 864), "format": "RGB888"}
            )
            self.picam2.configure(config)
            self.picam2.start()
            print(f"[INTERFACE] Camera Initialized.")

        except Exception as e:
            print(f"[INTERFACE] Failed to initialize camera: {e}")
            self.running = False
            if hasattr(self, 'vision_engine'):
                self.vision_engine.close()

    def capture_and_preprocess(self):
        img = self.picam2.capture_array()
        frame = Backend.convert_resize_image(img)
        # Convert RGB to BGR for OpenCV/Storage if needed, but Engine handles RGB.
        # Let's keep it raw for the engine which does its own conversion.
        return frame

    def close(self):
        self.running = False
        if hasattr(self, 'picam2'):
            self.picam2.stop()
        if hasattr(self, 'vision_engine'):
            self.vision_engine.close()
            print(f"[INTERFACE] Vision interface closed.")

    def get_image_description(self, prompt):
        if not hasattr(self, 'vision_engine'):
            yield "Vision engine not initialized"
            return
            
        frame = self.capture_and_preprocess()
        # vlm_inference is a generator, so we yield directly from it
        for result_sentence in self.vision_engine.vlm_inference(frame, SYSTEM_PROMPT, prompt):
            yield result_sentence

def start_vision_engine():
    if os.path.exists(SOCKET_PATH):
        os.remove(SOCKET_PATH)

    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server.bind(SOCKET_PATH)
    server.listen(1)
    
    bridge = DigitalEyeBridge()

    def signal_handler(sig, frame):
        print("\n[INTERFACE] Shutting down...")
        bridge.close()
        server.close()
        if os.path.exists(SOCKET_PATH):
            os.remove(SOCKET_PATH)
        sys.exit(0)

    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)

    print(f"[INTERFACE] Listening on {SOCKET_PATH}...")

    while True:
        print("[INTERFACE] Waiting for connection...")
        conn, _ = server.accept()
        
        # Wait for backend to be ready before acknowledging client
        while not bridge.vision_engine._isReady():
            time.sleep(1)
            
        ready_msg = json.dumps({'type': 'ready', 'text': {"answer": "Ready", "time": "0"}}) + "\n"
        print("[INTERFACE] Sending READY signal to client.")
        conn.sendall(ready_msg.encode('utf-8'))
        
        try:
            while True:
                data = conn.recv(1024)
                if not data: break                
                try:
                    command = data.decode('utf-8')
                    print(f"[INTERFACE] Received command: {command}")

                    if command == "take a picture":
                        frame = bridge.capture_and_preprocess()
                        # OpenCV expects BGR
                        frame_bgr = cv2.cvtColor(frame, cv2.COLOR_RGB2BGR)
                        frame_resized = bridge.picam2.capture_array()
                        timestamp = int(time.time())
                        
                        # Ensure directory exists
                        os.makedirs("./captures", exist_ok=True)
                        filename = f"./captures/captured_{timestamp}.jpg"
                        cv2.imwrite(filename, frame_resized)
                        
                        response_text = json.dumps({"type": "text", "text": {"answer": "Image saved", "time": "0"}})
                        print(f"[INTERFACE] Captured image saved to {filename}")
                        conn.sendall(response_text.encode('utf-8') + b"\n")

                    else:
                        for sentence in bridge.get_image_description(command):
                            response_text = json.dumps({
                                "type": "text", 
                                "text": {"answer": sentence, "time": str(time.time())}
                            })
                            print(f"[INTERFACE] Sending sentence: {sentence}")
                            conn.sendall(response_text.encode('utf-8') + b"\n")
                except json.JSONDecodeError:
                    print("[INTERFACE] JSON Decode Error")
                except Exception as e:
                    print(f"[INTERFACE] Error processing command: {e}")
                    
        except Exception as e:
            print(f"[INTERFACE] Socket connection error: {e}")
        finally:
            conn.close()

if __name__ == "__main__":
    start_vision_engine()