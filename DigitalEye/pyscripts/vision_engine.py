import time
import queue
import multiprocessing as mp
import numpy as np
import cv2
import re 
from hailo_platform import VDevice
from hailo_platform.genai import VLM

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