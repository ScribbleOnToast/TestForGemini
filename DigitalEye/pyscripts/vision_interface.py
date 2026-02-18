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
from hailo_platform import VDevice
from hailo_platform.genai import VLM
from picamera2 import Picamera2

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
class Backend:
    def __init__(self, hef_path: str, max_tokens: int = 200, temperature: float = 0.1,
                 seed: int = 42, system_prompt: str = 'You are a helpful assistant.') -> None:
        self.hef_path = hef_path
        self.max_tokens = max_tokens
        self.temperature = temperature
        self.seed = seed
        self.system_prompt = system_prompt

        self._request_queue = mp.Queue(maxsize=10)
        self._response_queue = mp.Queue(maxsize=100)
        self._process = mp.Process(
            target=vlm_worker_process,
            args=(self._request_queue, self._response_queue, self.hef_path, self.max_tokens, self.temperature, self.seed)
        )
        self._process.start()

    def vlm_inference(self, image: np.ndarray, system_prompt: str, user_prompt: str, timeout: int = 30):
        request_data = {
            'numpy_image': self.convert_resize_image(image),
            'prompts': {
                'system_prompt': system_prompt,
                'user_prompt': user_prompt,
            }
        }
        self._request_queue.put(request_data)

        while True:
            try:
                response = self._response_queue.get(timeout=timeout)                
                if response.get('status') == 'streaming':
                    yield response['chunk']
                
                elif response.get('status') == 'complete':
                    break
                
                elif response.get('status') == 'error':
                    yield f"Error: {response['error']}"
                    break
                    
                elif response.get('init'):
                    continue
                    
            except mp.TimeoutError:
                yield "Error: Timeout"
                break
            except Exception as e:
                yield f"Error: {str(e)}"
                break

    def _cleanup_queues(self) -> None:
        while not self._request_queue.empty():
            try: self._request_queue.get_nowait()
            except: break
        while not self._response_queue.empty():
            try: self._response_queue.get_nowait()
            except: break

    def _isReady(self) -> bool:
        if getattr(self, "_ready", False):
            return self._process.is_alive()
        if not self._process.is_alive():
            return False
        try:
            msg = self._response_queue.get(timeout=0.01)
            if isinstance(msg, dict) and msg.get('init') == 'VLM initialized':
                self._ready = True
                return True
            self._response_queue.put(msg)
        except queue.Empty:
            pass
        return False

    @staticmethod
    def convert_resize_image(image_array: np.ndarray, target_size: tuple[int, int] = (336, 336)) -> np.ndarray:
        if len(image_array.shape) == 3 and image_array.shape[2] == 3:
            image_array = cv2.cvtColor(image_array, cv2.COLOR_BGR2RGB)
        h, w = image_array.shape[:2]
        target_w, target_h = target_size
        scale = max(target_w / w, target_h / h)
        new_w, new_h = int(w * scale), int(h * scale)
        resized = cv2.resize(image_array, (new_w, new_h), interpolation=cv2.INTER_LINEAR)
        x_start, y_start = (new_w - target_w) // 2, (new_h - target_h) // 2
        cropped = resized[y_start:y_start+target_h, x_start:x_start+target_w]
        return cropped.astype(np.uint8)

    def close(self) -> None:
        try:
            self._request_queue.put(None)
            self._process.join(timeout=2)
            if self._process.is_alive():
                self._process.terminate()
        except Exception:
            pass

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

def vlm_worker_process(request_queue: mp.Queue, response_queue: mp.Queue, hef_path: str,
                      max_tokens: int, temperature: float, seed: int) -> None:
    try:
        params = VDevice.create_params()
        params.group_id = "SHARED"
        vdevice = VDevice(params)
        vlm = VLM(vdevice, hef_path)
        print("[ENGINE] VLM initialized.")
        response_queue.put({'init': 'VLM initialized', 'error': None})
        while True:
            item = request_queue.get()
            if item is None:
                break

            try:
                result = _hailo_inference_inner(
                    item['numpy_image'],
                    item['prompts'],
                    vlm,
                    max_tokens,
                    temperature,
                    seed, 
                    response_queue
                )
                response_queue.put({'status': 'complete', 'error': None})
                response_queue.put({'result': result, 'error': None})
            except Exception as e:
                response_queue.put({'status': 'error', 'error': str(e)})
    except Exception as e:
            response_queue.put({'status': 'error', 'error': str(e)})
    finally:
        try:
            vlm.release()
            vdevice.release()
        except Exception as e:
            print(f"Error during cleanup in worker: {e}")

def _hailo_inference_inner(image: np.ndarray, prompts: dict, vlm: VLM,
                          max_tokens: int, temperature: float, seed: int, response_queue: mp.Queue) -> dict:
    try:
        start_time = time.time()
        prompt = [
            {
                "role": "system",
                "content": [{"type": "text", "text": prompts["system_prompt"]}]
            },
            {
                "role": "user",
                "content": [
                    {"type": "image"},
                    {"type": "text", "text": prompts["user_prompt"]}
                ]
            }
        ]     

        # Buffer now holds raw text, not a list of tokens
        text_buffer = ""
        print(prompts)
        print(prompt)
        with vlm.generate(prompt=prompt, frames=[image], temperature=temperature, seed=seed, max_generated_tokens=max_tokens) as generation:
            for chunk in generation:
                if chunk == '<|im_end|>':
                    continue
                print(chunk)  # For debugging
                text_buffer += chunk
                
                # Split using lookbehind to keep the punctuation attached to the sentence
                # Matches: (. or ? or !) followed by whitespace
                sentences = re.split(r'(?<=[.!?])\s+', text_buffer)

                if len(sentences) > 1:
                    # We have at least one complete sentence.
                    # The last item in the list is the "remainder" (incomplete sentence).
                    complete_sentences = sentences[:-1]
                    text_buffer = sentences[-1]

                    for sent in complete_sentences:
                        if sent.strip():
                             response_queue.put({'status': 'streaming', 'chunk': sent.strip()})

            # Flush any remaining text in the buffer when generation ends
            if text_buffer.strip():
                response_queue.put({'status': 'streaming', 'chunk': text_buffer.strip()})

        vlm.clear_context()
        return {'answer': 'Generation Complete', 'time': f"{time.time() - start_time:.2f}"}
        
    except Exception as e:
        return {
            'answer': f'Error: {str(e)}',
            'time': f"{time.time() - start_time:.2f} seconds"
        }

if __name__ == "__main__":
    start_vision_engine()