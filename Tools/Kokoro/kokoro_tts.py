#!/usr/bin/env python3
"""One-shot Kokoro fallback used when the persistent server is unavailable."""
import argparse
import numpy as np
import wave
from kokoro import KPipeline

parser = argparse.ArgumentParser()
parser.add_argument("--file", required=True)
parser.add_argument("--output", required=True)
parser.add_argument("--voice", default="af_bella")
parser.add_argument("--speed", type=float, default=1.0)
parser.add_argument("--lang", default="a")
args = parser.parse_args()

with open(args.file, "r", encoding="utf-8") as source:
    text = source.read().strip()

chunks = []
for _, _, audio in KPipeline(lang_code=args.lang if args.lang in ("a", "b") else "a")(
    text, voice=args.voice, speed=max(0.5, min(2.0, args.speed))
):
    if audio is not None:
        chunks.append(audio.cpu().numpy() if hasattr(audio, "cpu") else np.array(audio))

if not chunks:
    raise RuntimeError("No audio generated")

audio = np.concatenate(chunks)
with wave.open(args.output, "wb") as wav:
    wav.setnchannels(1)
    wav.setsampwidth(2)
    wav.setframerate(24000)
    wav.writeframes((audio * 32767).astype(np.int16).tobytes())

print(f"OK:{len(audio)}", flush=True)
