# iPhone Phone Remote

The iPhone remote is a local-only HTTPS web page hosted by the desktop app. It lets an iPhone on the same Wi-Fi use Safari microphone input and phone speaker output while the model, Whisper, TTS, memories, and chat history stay on the PC.

## Use It

1. Start the desktop app.
2. In the left panel, find `IPHONE REMOTE`.
3. Leave the port at `5100` unless it conflicts with another app.
4. Optional: set a PIN.
5. Click `Start Phone Remote`.
6. Click `Open Certificate Folder`.
7. Send/open `voicechatbot-phone-remote.cer` on the iPhone.
8. On the iPhone, install the profile, then fully trust it in iOS certificate trust settings.
9. Click `Copy Phone URL` in the desktop app and open that URL in Safari on the iPhone.

Safari microphone access requires a secure context, so HTTPS is required even on the local network. The app generates a local certificate under:

`%APPDATA%\VoiceChatbot\phone-remote-cert`

If the PC's Wi-Fi IP address changes, start the phone remote again and reinstall the newly generated `.cer` if Safari warns about the certificate.

## Behavior

- Hold `Hold to Talk` on the iPhone, speak, then release.
- The browser records 16 kHz mono WAV audio locally and sends it to the PC over HTTPS.
- The PC transcribes with local Whisper, sends the text to the configured Ollama model, generates optional local TTS audio, and returns that audio to the iPhone.
- Remote messages are also added to the desktop chat as `[iPhone] ...`.

## Security Notes

- This is intended for the same trusted Wi-Fi network only.
- Do not port-forward this service to the internet.
- Use the PIN if other people share the network.
- No audio is uploaded to a cloud service by this feature.
