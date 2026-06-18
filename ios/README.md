# VoiceChatbot for iPhone

This directory contains the native SwiftUI client for the existing VoiceChatbot PC server.

The PC remains responsible for Ollama/llama.cpp, Whisper transcription, Kokoro speech,
ComfyUI, documents, and model control. The iPhone app is a secure LAN/VPN client.

## Current first pass

- Native chat using the existing phone remote API.
- Continuous conversation using `AVAudioSession` and `AVAudioEngine`.
- Audio background mode for minimized and Lock Screen operation during a user-started session.
- Voice activity detection and 16 kHz WAV uploads to PC Whisper.
- Playback of speech returned by the PC.
- Lock Screen play, pause, and stop commands.
- Dedicated long-running transcription mode with timestamps and Share export.
- Wi-Fi/VPN server profiles and imported certificate pinning.

## Generate the Xcode project

Install [XcodeGen](https://github.com/yonaskolb/XcodeGen), then run:

```sh
cd ios
xcodegen generate
open VoiceChatbot.xcodeproj
```

Choose your Apple development team and run on a physical iPhone. Background microphone
behavior must be validated on a device; Simulator is useful for UI and networking checks
but is not authoritative for screen-lock audio behavior.

Every push to the iOS branch is also compiled by `.github/workflows/ios-build.yml` on
GitHub's macOS runner.

For the complete Mac, signing, iPhone installation, certificate, and screen-lock test steps,
see [`MAC_INSTALL.md`](MAC_INSTALL.md).

## PC setup

1. Enable **Phone Remote** in the Windows app.
2. Export the generated `.cer` file with **Open Certificate Folder**.
3. Transfer the `.cer` file to the iPhone and import it in the app's Connection tab.
4. Enter the LAN URL while at home and the VPN-reachable URL while away.
5. Start a conversation before locking the phone.

## iOS constraints

- The microphone continues only for an explicit active audio session.
- Force-quitting the app ends the session.
- Phone calls, Siri, route changes, and OS resource pressure may interrupt audio.
- The app must visibly indicate when a conversation is active and provide an immediate stop action.
