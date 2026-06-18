# Voice Chatbot

Voice Chatbot is a Windows desktop and phone-browser client for local AI models. It supports voice conversation, local Whisper transcription, Ollama, OpenAI-compatible llama.cpp servers, multimodal image prompts, web search, ComfyUI image/video workflows, Kokoro speech, and SSH-based model switching.

A native SwiftUI iPhone client is under development in [`ios/`](ios/README.md). It keeps
models and transcription on the PC while adding native background audio support for
user-started conversations over Wi-Fi or VPN.

## Download and install

For a normal Windows installation, download the latest `VoiceChatbot-Setup-*-win-x64.exe` from GitHub Releases and run it.

The installer is not code-signed yet, so Windows SmartScreen may show an **Unknown publisher** warning. Verify the SHA-256 checksum published with the release before running it.

The installer:

- Installs a self-contained Windows x64 build. The .NET runtime is included.
- Creates Start Menu shortcuts.
- Optionally creates a desktop shortcut.
- Includes local face-detection resources and Kokoro helper scripts.
- Leaves settings and conversation data in `%APPDATA%\VoiceChatbot`.

Windows 10 version 1809 or newer, or Windows 11, is required.

## Minimum setup

You need at least one chat backend.

### Option 1: Ollama

1. Install [Ollama for Windows](https://ollama.com/download/windows).
2. Pull a model, for example:

   ```powershell
   ollama pull gemma3:4b
   ```

3. In Voice Chatbot select `Ollama`.
4. Set the Ollama URL to `http://localhost:11434`.
5. Click **Refresh Models** and select the model.

### Option 2: llama.cpp or another OpenAI-compatible server

1. Install or build [llama.cpp](https://github.com/ggml-org/llama.cpp).
2. Start `llama-server.exe` with your GGUF model.
3. For image-capable models, use the matching multimodal projector or media embedder required by that model.
4. In Voice Chatbot select `OpenAI-compatible`.
5. Set the URL to the server's `/v1` endpoint, for example `http://192.168.1.50:8080/v1`.
6. Click **Refresh Models**.

The server controls and Hermes commands expect model batch files in `C:\llama.cpp` on the SSH host. The desktop model dropdown discovers files named `start-*.bat`.

## Voice requirements

### Speech input

Local transcription uses Whisper.net. Use **Download Model** inside the app to download a Whisper model. A microphone is required for voice input.

If **Listen** does not start on a new PC:

1. Select `Whisper.net` as the transcription backend.
2. Select `base` (or another size) under Whisper Model.
3. Click **Download Model** once.
4. Select the microphone and press **Listen** again.

The app reports microphone-open and missing-model errors directly in the chat. If a saved microphone cannot be opened, it retries the Windows default input device.

Whisper model guidance:

- `tiny`: fastest and least accurate.
- `base`: lightweight general use.
- `small`: recommended balance of speed and recognition accuracy.
- `medium`: highest accuracy in the dropdown, but much larger and slower.

Optional transcription tools:

- Ryzen AI Whisper uses this default external command:
  `call "%USERPROFILE%\VoiceChatbot\tools\ryzen-ai-whisper-transcribe.bat" {input}`
- `ffmpeg` is needed for some phone audio formats.

### Kokoro speech output

Kokoro is optional. Without it, other available Windows speech paths may still work.

To install local Kokoro:

1. Install Python 3.11, 3.12, or 3.13 x64 from [python.org](https://www.python.org/downloads/windows/). Enable **Add Python to PATH**.
2. Open PowerShell in the installed app's `Tools\Kokoro` folder.
3. Run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install-kokoro.ps1
   ```

Voice Chatbot starts the bundled local Kokoro server on `http://127.0.0.1:8765` when needed. To use a specific Python executable, set the `VOICECHATBOT_PYTHON` environment variable to its full path.

## Optional integrations

### Web search

Create a key at [Tavily](https://tavily.com), enter it in the app, and enable **Web Search**. API keys are stored locally in `%APPDATA%\VoiceChatbot\settings.json`; they are not included in the installer or repository.

### ComfyUI

Install [ComfyUI](https://github.com/comfyanonymous/ComfyUI), install the models and custom nodes required by your workflows, then set the ComfyUI URL in the app. The default local URL is `http://localhost:8000`.

The app's SSH start/stop buttons expect these batch files on the model host:

- `C:\llama.cpp\Start-ComfyUI-LAN.bat`
- `C:\llama.cpp\Stop-ComfyUI-LAN.bat`

### SSH model control

SSH model control is optional. Configure the host, port, username, and password in the app. The remote Windows machine must expose SSH and make its `C:` drive available to the SSH environment as `/mnt/c`.

The application currently recognizes common batch files for Gemma, GPT-OSS, Mistral, Qwen, and LFM. The desktop dropdown also discovers additional `start-*.bat` files.

### Phone HTTPS remote

Enable **Phone Remote**, choose a LAN port, and optionally set a PIN. The app creates a local certificate. Install and trust the generated `.cer` certificate on the phone, then open the displayed HTTPS URL while both devices are on the same network.

### YouTube and document tools

The Windows installer bundles `yt-dlp`, Deno, and `ffmpeg`, so YouTube captions and audio fallback work on a clean installation. Release builds fetch current official Windows binaries from their projects.

Optional tools improve document support:

- Poppler `pdftoppm` and Tesseract OCR for scanned PDFs.

These tools can be installed with WinGet where packages are available.

## Building from source

Requirements:

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer

Build and run:

```powershell
dotnet restore
dotnet build
dotnet run
```

Build the installer and portable ZIP:

```powershell
winget install --id JRSoftware.InnoSetup -e
.\scripts\Build-Installer.ps1 -Version 1.0.0
```

Outputs are written to `artifacts\`.

## GitHub releases

The included GitHub Actions workflow builds the installer and portable ZIP:

- Run **Build Windows release** manually from the Actions tab for test artifacts.
- Push a tag such as `v1.0.0` to create a GitHub Release automatically.

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## Data and security

- Settings, API keys, downloaded Whisper models, generated media, memories, and phone certificates are stored outside the installation directory under `%APPDATA%\VoiceChatbot`.
- Do not commit `settings.json`, certificates, passwords, API keys, model files, or private batch files.
- SSH passwords are stored in the local settings file. Use a dedicated LAN account and restrict network access appropriately.
- Uninstalling the application does not delete `%APPDATA%\VoiceChatbot`, so reinstalling preserves settings. Delete that folder manually to remove all local app data.
