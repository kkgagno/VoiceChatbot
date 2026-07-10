# Voice Chatbot Android

Native Android client for the existing VoiceChatbot PC remote API.

## What this includes

- Chat tab using the same `/api/message`, `/api/respond`, `/api/speak`, and media endpoints as the iPhone app.
- One-shot microphone capture and PC transcription.
- Background conversation mode using an Android foreground microphone service, so Android keeps the mic alive while minimized/screen-off.
- Attach images/documents to chat prompts.
- Models tab with the same Hermes model-switch commands.
- ComfyUI tab for image/edit/video prompts through the PC app.
- Krea2 tab using:
  - `GET /api/krea2/options`
  - `POST /api/krea2/create`
- Connection tab with Home Wi-Fi and Tailscale/VPN URLs.
- Calendar and SMS draft handoff through Android system intents.

## Open/build

1. Install Android Studio.
2. Open this folder:

   `android`

3. Let Android Studio sync Gradle.
4. Connect the Pixel with USB debugging enabled.
5. Press Run.

Default app ID:

`com.voicechatbot.android`

## Default PC URLs

The app defaults to:

- Home Wi-Fi: `https://192.168.4.114:5100`
- VPN/Tailscale: `http://minilagertha.tail2762b8.ts.net:5101`

Change these in the app under More -> Connection.

## Android-specific notes

- Background microphone requires a visible foreground notification. This is normal Android behavior.
- The app currently trusts the PC HTTPS certificate for local/private use so it can work with your existing self-signed phone remote certificate. For Play Store/public distribution this should be replaced with certificate pinning.
- SMS sending opens Android's Messages composer for confirmation; Android does not allow silent SMS sending for a normal app.
- Calendar creation opens Android's calendar insert screen for confirmation.
- Android Health Connect is stubbed in this first pass. Live health data needs a dedicated Health Connect integration pass.

## Existing PC remote endpoints used

- `GET /api/status`
- `POST /api/transcribe`
- `POST /api/vad`
- `POST /api/respond`
- `POST /api/message`
- `POST /api/tool`
- `POST /api/grounded-answer`
- `POST /api/text-message`
- `POST /api/calendar-draft`
- `POST /api/speak`
- `GET /api/krea2/options`
- `POST /api/krea2/create`
