#!/usr/bin/env python3
"""Always-on wake word detector used by Voice Chatbot, built on openWakeWord.

Reads raw 16 kHz, 16-bit, mono PCM from stdin in 1280-sample (80 ms) frames and prints one JSON
line per event to stdout:

    {"event": "ready", "model": "hey_jarvis"}
    {"event": "wake", "model": "hey_jarvis", "score": 0.93}
    {"event": "error", "message": "...", "code": "missing_package"}

The process exits when stdin is closed. Errors are fatal: one "error" line, then exit.
Run with --download-only to fetch the pretrained models without listening (used by the installer).
"""
import argparse
import json
import os
import sys
import time
import warnings

FRAME_SAMPLES = 1280
FRAME_BYTES = FRAME_SAMPLES * 2
COOLDOWN_SECONDS = 2.0
# The app stops sending audio while it listens or speaks. A longer wait than this between frames
# means the stream was paused, so the old audio (often the wake word itself) is cleared first.
RESUME_GAP_SECONDS = 1.0
PRETRAINED_MODELS = ("hey_jarvis", "alexa", "hey_mycroft", "hey_rhasspy")

warnings.filterwarnings("ignore")


def emit(event, **fields):
    payload = {"event": event}
    payload.update(fields)
    try:
        sys.stdout.write(json.dumps(payload) + "\n")
        sys.stdout.flush()
    except (BrokenPipeError, OSError):
        # The app went away; nothing left to report to.
        sys.exit(0)


def read_frame(stream):
    """Returns exactly FRAME_BYTES bytes, or None at end of input."""
    data = bytearray()
    while len(data) < FRAME_BYTES:
        chunk = stream.read(FRAME_BYTES - len(data))
        if not chunk:
            return None
        data.extend(chunk)
    return bytes(data)


def download(names):
    # Skips files that are already there, so this only needs the network on first use.
    from openwakeword.utils import download_models
    download_models(list(names))


def load_model(name):
    from openwakeword.model import Model
    download([name])
    return Model(wakeword_models=[name], inference_framework="onnx")


def remove_downloaded(name):
    """Deletes a model's files (and the shared feature models) so the next download starts fresh."""
    import openwakeword
    folder = os.path.join(os.path.dirname(os.path.abspath(openwakeword.__file__)), "resources", "models")
    if not os.path.isdir(folder):
        return
    for file_name in os.listdir(folder):
        if file_name.startswith((name + "_v", "melspectrogram.", "embedding_model.")):
            os.remove(os.path.join(folder, file_name))


def download_all():
    """Downloads every pretrained model and checks that it loads, replacing broken downloads once."""
    download(PRETRAINED_MODELS)
    for name in PRETRAINED_MODELS:
        try:
            load_model(name)
        except ImportError:
            raise
        except Exception:
            remove_downloaded(name)
            load_model(name)
        print(f"{name}: ready")


def listen(model, name, threshold):
    import numpy as np

    stdin = sys.stdin.buffer
    last_wake = float("-inf")
    last_frame = time.monotonic()
    while True:
        frame = read_frame(stdin)
        if frame is None:
            return
        now = time.monotonic()
        if now - last_frame > RESUME_GAP_SECONDS:
            model.reset()
        last_frame = now

        scores = model.predict(np.frombuffer(frame, dtype=np.int16))
        score = max((float(s) for s in scores.values()), default=0.0)
        if score >= threshold and now - last_wake >= COOLDOWN_SECONDS:
            last_wake = now
            # Forget this utterance so it cannot fire again once the cooldown ends.
            model.reset()
            emit("wake", model=name, score=round(score, 3))


def main():
    parser = argparse.ArgumentParser(description="openWakeWord detector for Voice Chatbot")
    parser.add_argument("--model", default="hey_jarvis", help="pretrained model name, e.g. hey_jarvis or alexa")
    parser.add_argument("--threshold", type=float, default=0.5, help="score (0-1) needed to report a wake word")
    parser.add_argument("--download-only", action="store_true", help="download the pretrained models and exit")
    args = parser.parse_args()

    name = args.model.strip().lower().replace(" ", "_") or "hey_jarvis"
    threshold = min(max(args.threshold, 0.05), 0.99)

    if args.download_only:
        try:
            download_all()
        except ImportError as ex:
            print(f"openWakeWord is not installed: {ex}", file=sys.stderr)
            return 2
        except Exception as ex:
            print(f"Could not download the wake word models: {ex}", file=sys.stderr)
            return 3
        print("Wake word models are ready.")
        return 0

    try:
        model = load_model(name)
    except ImportError as ex:
        emit("error", code="missing_package",
             message=f"openWakeWord is not installed ({ex}). Run Tools\\WakeWord\\install-wakeword.ps1.")
        return 2
    except Exception as ex:
        emit("error", code="model_failed",
             message=f"Could not load the '{name}' wake word model: {ex}. "
                     "Re-run install-wakeword.ps1 to download it again.")
        return 3

    emit("ready", model=name)
    try:
        listen(model, name, threshold)
    except KeyboardInterrupt:
        pass
    except Exception as ex:
        emit("error", code="failed", message=f"Wake word detection stopped: {ex}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
