#!/usr/bin/env python3
"""Persistent local Kokoro TTS server used by Voice Chatbot."""
import argparse
import hmac
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
MAX_REQUEST_BYTES = 1024 * 1024
# Set at launch: WAV files are only written inside OUT_DIR, and every request must carry TOKEN.
OUT_DIR = ""
TOKEN = ""
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


def resolve_output_path(output):
    """Returns the real path for a .wav file inside OUT_DIR, or raises ValueError."""
    if not output or not OUT_DIR:
        raise ValueError("No output path")
    base = os.path.normcase(os.path.realpath(OUT_DIR))
    path = os.path.realpath(output)
    if not path.lower().endswith(".wav"):
        raise ValueError("Output must be a .wav file")
    try:
        inside = os.path.commonpath([base, os.path.normcase(path)]) == base
    except ValueError:  # different drives on Windows
        inside = False
    if not inside or os.path.normcase(path) == base:
        raise ValueError("Output path is outside the speech folder")
    return path


def generate_wav(text, output, voice, lang, speed):
    if not text.strip():
        raise ValueError("Empty text")
    output = resolve_output_path(output)
    os.makedirs(os.path.dirname(output), exist_ok=True)
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

    def is_authorized(self):
        # Browsers send Origin on cross-site requests; the app never does.
        if self.headers.get("Origin") is not None:
            return False
        supplied = self.headers.get("X-Kokoro-Token", "")
        return bool(TOKEN) and hmac.compare_digest(supplied.encode("utf-8"), TOKEN.encode("utf-8"))

    def do_GET(self):
        if self.path != "/health":
            self.send_json(404, {"ok": False})
        elif not self.is_authorized():
            self.send_json(403, {"ok": False, "error": "Forbidden"})
        else:
            self.send_json(200, {"ok": True, "voices": VALID_VOICES})

    def do_POST(self):
        if self.path != "/tts":
            self.send_json(404, {"ok": False})
            return
        if not self.is_authorized():
            self.send_json(403, {"ok": False, "error": "Forbidden"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > MAX_REQUEST_BYTES:
                self.send_json(413, {"ok": False, "error": "Bad request size"})
                return
            request = json.loads(self.rfile.read(length).decode("utf-8"))
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
    parser.add_argument("--out-dir", required=True, help="the only folder WAV files may be written to")
    parser.add_argument("--token", required=True, help="secret every request must send as X-Kokoro-Token")
    args = parser.parse_args()
    global OUT_DIR, TOKEN
    OUT_DIR = os.path.abspath(args.out_dir)
    TOKEN = args.token
    os.makedirs(OUT_DIR, exist_ok=True)
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"KOKORO_SERVER_READY:http://{args.host}:{args.port}", flush=True)
    threading.Thread(
        target=lambda: [get_pipeline(lang.strip()) for lang in args.preload.split(",") if lang.strip()],
        daemon=True,
    ).start()
    server.serve_forever()


if __name__ == "__main__":
    main()
