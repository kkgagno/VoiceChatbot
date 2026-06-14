#!/usr/bin/env python3
"""Persistent local Kokoro TTS server used by Voice Chatbot."""
import argparse
import json
import os
import re
import sys
import threading
import warnings
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np

warnings.filterwarnings("ignore")
_PIPELINES = {}
_PIPELINE_LOCK = threading.Lock()
_TTS_LOCK = threading.Lock()
VALID_VOICES = "af_bella,af_nicole,af_sarah,af_sky,am_adam,am_michael,bf_emma,bf_isabella,bm_george,bm_lewis"


def split_text(text, max_chars=500):
    sentences = re.split(r"(?<=[.!?])\s+", text)
    chunks, current = [], ""
    for sentence in sentences:
        sentence = sentence.strip()
        if not sentence:
            continue
        if len(current) + len(sentence) + 1 > max_chars and current:
            chunks.append(current)
            current = sentence
        else:
            current = f"{current} {sentence}".strip()
    if current:
        chunks.append(current)
    return chunks


def get_pipeline(lang):
    lang = lang if lang in ("a", "b") else "a"
    with _PIPELINE_LOCK:
        if lang not in _PIPELINES:
            from kokoro import KPipeline
            _PIPELINES[lang] = KPipeline(lang_code=lang)
        return _PIPELINES[lang]


def generate_wav(text, output, voice, lang, speed):
    if not text.strip():
        raise ValueError("Empty text")
    os.makedirs(os.path.dirname(output) or ".", exist_ok=True)
    with _TTS_LOCK:
        audio_chunks = []
        for chunk in split_text(text):
            for _, _, audio in get_pipeline(lang)(chunk, voice=voice, speed=max(0.5, min(2.0, float(speed)))):
                if audio is not None:
                    audio_chunks.append(audio.cpu().numpy() if hasattr(audio, "cpu") else np.array(audio))
    if not audio_chunks:
        raise RuntimeError("No audio generated")
    audio = np.concatenate(audio_chunks)
    import wave
    with wave.open(output, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(24000)
        wav.writeframes((audio * 32767).astype(np.int16).tobytes())
    return len(audio)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        print(fmt % args, file=sys.stderr, flush=True)

    def send_json(self, code, payload):
        data = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self.send_json(200, {"ok": True, "voices": VALID_VOICES}) if self.path == "/health" else self.send_json(404, {"ok": False})

    def do_POST(self):
        if self.path != "/tts":
            self.send_json(404, {"ok": False})
            return
        try:
            request = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))).decode("utf-8"))
            samples = generate_wav(
                str(request.get("text", "")),
                str(request.get("output", "")),
                str(request.get("voice", "af_bella")),
                str(request.get("lang", "a")),
                float(request.get("speed", 1.0)),
            )
            self.send_json(200, {"ok": True, "samples": samples})
        except Exception as exc:
            self.send_json(500, {"ok": False, "error": f"{type(exc).__name__}: {exc}"})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--preload", default="a")
    args = parser.parse_args()
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"KOKORO_SERVER_READY:http://{args.host}:{args.port}", flush=True)
    threading.Thread(
        target=lambda: [get_pipeline(lang.strip()) for lang in args.preload.split(",") if lang.strip()],
        daemon=True,
    ).start()
    server.serve_forever()


if __name__ == "__main__":
    main()
