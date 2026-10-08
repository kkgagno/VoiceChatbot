# Wake word models

The always-on wake word detector (`WakeWordDetector`, `Core/OpenWakeWordPipeline.cs`) runs these
[openWakeWord](https://github.com/dscripka/openWakeWord) ONNX models in-process with ONNX Runtime.
They ship with the app, so nothing else needs to be installed.

Downloaded unchanged from the openWakeWord v0.5.1 release
(`https://github.com/dscripka/openWakeWord/releases/download/v0.5.1/<file>`):

| File | Purpose |
| --- | --- |
| `melspectrogram.onnx` | 16 kHz audio to mel spectrogram frames |
| `embedding_model.onnx` | Google `speech_embedding` features (76 mel frames to 96 values) |
| `hey_jarvis_v0.1.onnx` | "Hey Jarvis" (default) |
| `alexa_v0.1.onnx` | "Alexa" |
| `hey_mycroft_v0.1.onnx` | "Hey Mycroft" |
| `hey_rhasspy_v0.1.onnx` | "Hey Rhasspy" |

## License

openWakeWord's code is Apache 2.0, but its pre-trained models are licensed **CC BY-NC-SA 4.0**
(Creative Commons Attribution-NonCommercial-ShareAlike 4.0): they may be used and shared for
**non-commercial purposes only**, with attribution to openWakeWord (David Scripka), under the same
license. Treat every file in this folder that way. A commercial build would need other wake word models.
