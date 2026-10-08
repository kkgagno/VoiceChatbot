# Voice Chatbot

Voice Chatbot is a Windows desktop and phone-browser client for local AI models. It supports voice conversation, local Whisper transcription, Ollama, OpenAI-compatible llama.cpp servers, multimodal image prompts, web search, ComfyUI image/video workflows, Kokoro speech, and SSH-based model switching.

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

### Tools and formatting

With **Let the model use tools** on (Chat Backend, on by default), the model decides by itself when to search the web (needs Web Search on and a Tavily key), check the current date and time, get a stock quote, read a web page, or save a memory. A short note appears in the chat for each tool it uses. So that a web page cannot plant memories, the model cannot save a memory in an answer where it has already read web results; ask it to remember things in a separate message. The Hermes, Pi, image and video commands work as before, and "search the web for ..." or "look it up online" still forces a search.

Tools need a model and server with function calling: in Ollama, a model tagged *tools* (for example `qwen3` or `llama3.1`); in llama.cpp, start `llama-server` with `--jinja`. If the model cannot use tools, the app shows one note, turns tools off for that model until restart and answers normally. The phone remote and scheduled prompts do not use tools yet.

Finished replies are shown formatted: headings, bold, italic and strikethrough text, inline code, bullet, numbered and task lists, quotes, rules, tables and links (links open in your browser; only `http`, `https` and `mailto`). Code blocks keep their **Copy** button. While a reply streams it shows as plain text, and formatting is never read aloud. The phone remote and the scheduler window show plain text. Turn off **Format replies (Markdown)** in Chat Backend to get plain text in the chat too. The default system prompt now allows light Markdown; an unchanged old default is updated automatically, but if you wrote your own prompt, remove any "no Markdown" instruction from it to get formatted replies.

### Hide model thinking

**Hide model thinking** (Chat Backend, on by default, saved as `DisableModelThinking`) asks the server to skip the model's thinking phase, so replies start sooner:

- llama.cpp and other OpenAI-compatible servers get `"chat_template_kwargs": {"enable_thinking": false}` and `"reasoning_format": "deepseek"`; thinking a model still writes then arrives separately and is ignored. These fields are not sent to `api.openai.com`.
- Ollama gets `"think": false`.
- A server that answers HTTP 400 to these fields gets the request again without them, and is then sent requests without them until the app restarts.

Some local models write plain-text planning notes before (or instead of) their answer, such as `The user said "hi". Wait, they might want more. Let's try: "Hi!"`. These are never shown, saved or spoken, whether the switch is on or off: while the reply streams the bubble shows "Thinking...", and the finished bubble keeps only the answer (the text after the notes, after "Final answer:", "Response:" or "Reply:", or the quoted reply after "Let's try:"). When there is no answer at all, the bubble says so and nothing is spoken. The hidden notes are written to the app log. `<think>...</think>` blocks are removed as before.

## Voice requirements

### Speech input

Local transcription uses Whisper.net. Use **Download Model** inside the app to download a Whisper model. A microphone is required for voice input.

Whisper can run on the graphics card: the installer includes the Vulkan build of Whisper.net, which works on AMD Radeon (including Ryzen integrated graphics), NVIDIA and Intel GPUs with a current driver. Turn on **Use GPU for speech recognition** under **Voice Input** (off by default, saved as `WhisperUseGpu`) to try it. It can be faster, but on some AMD integrated GPUs it repeats or drops words (every sentence arriving twice, or no reply at all); turn it off if that happens. Without a usable Vulkan GPU, or if it fails to load, Whisper falls back to the CPU automatically. The line under the switch shows what the model runs on, for example `Whisper: Vulkan GPU (AMD Radeon(TM) 780M)` or `Whisper: CPU`. Changing it reloads the model; switching to the GPU after starting with it off needs a restart. Updating from 1.0.16, which had it on by default, turns it off once.

Each utterance is transcribed on its own, without the previous one as context, and a sentence Whisper repeats back to back ("This is bullshit.This is bullshit.") is sent once.

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
  Whisper on the AMD Ryzen AI NPU cannot be bundled with the installer: it needs AMD's Ryzen AI Software and NPU driver installed separately. The bundled GPU Whisper above needs neither.
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

#### Remote Kokoro server

To use Kokoro running on another machine (for example [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI)), open **Voice Output** in the settings sidebar and enter its address in **Remote Kokoro host**. Any of these forms work:

- `192.168.1.50` (port `8880` is assumed)
- `192.168.1.50:8880`
- `http://tts-box:8880/v1` or the full `.../v1/audio/speech` URL

Click **Test** to check the connection. If the server lists its voices, the voice dropdown is refreshed with them. **Engine** controls the fallback:

- `Auto` tries the remote host first and falls back to local Kokoro. After a failure it skips the remote host for 60 seconds so replies are not delayed.
- `Remote only` never starts the local server.
- `Local only` ignores the remote host.

Leave the host blank to use only the bundled local server. The host is saved as `KokoroRemoteUrl` in `settings.json`.

### Speaking replies

A finished reply is spoken in one go: the whole reply goes to Kokoro in one request and then plays, which sounds the most natural. The **Replay Audio** and **Download Audio** buttons appear when it starts playing. **Stop** (or **Esc**) and the microphone button stop speech straight away, also while the audio is still being prepared; a reply that was stopped or cancelled is not spoken. After the reply, hands-free listening and the wake word resume as usual. Code blocks are never read aloud. (The **Interrupt by speaking** option has been removed; its old settings are ignored.)

To have the assistant start talking while the model is still writing, turn on **Start speaking before the reply finishes** under **Voice Output** (off by default, needs **Stream responses**, saved as `StreamingSpeechEnabled`). The reply is then spoken in parts of at least about 120 characters, so Kokoro never gets a single word or short sentence on its own, and the next part is rendered while the current one plays. Replies to code or script requests still wait until they are complete. With this switch on, the Replay/Download buttons appear once the whole reply has been spoken. Updating turns the switch off once, because the earlier sentence-by-sentence speech sounded garbled; turn it back on if you prefer it.

### Wake word ("Hey Onyx")

Turn on **Only respond after the wake word** under **Voice Input** to have the app keep listening but answer only when you address it. Turning it on also turns on **Auto** (continuous listening), and it starts waiting again when the app opens. The status shows `Waiting for "hey onyx"`.

- Say the wake word and your request together: "Hey Onyx, what's the weather?" sends "what's the weather?".
- Or say just "Hey Onyx", wait for the Windows "Asterisk" sound, then ask within about 8 seconds. That one request needs no wake word.
- Everything else is ignored: no chat message and no reply (it is written to the log), and the app keeps listening. After each reply it waits for the wake word again.
- **Listen**, **Mic**, the global hotkey and the tray's Listen item start a turn that does not need the wake word.
- Change the phrase in the **Wake word** box (default `hey onyx`). It is matched in the Whisper transcript, ignoring case and punctuation, with the usual spellings of "hey" (hay, hi, hei, a) and small spelling differences in the name ("Hey, Onix.", "Hey Annex", "Hey on X"), but not other names such as "Hey Annie".
- Saved as `AutoDetectVoice` (false when the switch is on) and `WakeWord` in `settings.json`. The old default "hey assistant" becomes "hey onyx"; a phrase you typed is kept. The openWakeWord detector ("Hey Jarvis") from 1.0.16 has been removed.

### Live Transcriber

**Transcribe** (next to **Listen** and **Mic**) opens the Live Transcriber, a separate window for meetings, calls and videos. It remembers its size, position, pane heights and text size (saved as `Transcriber` in `settings.json`).

- **Source**: **Microphone** (the microphone selected under **Voice Input**) or **PC audio** (everything the PC plays, recorded from the default speakers or headphones). Switching while recording carries on with the new source.
- Audio is cut into chunks at natural pauses (half a second of silence once a chunk is 2 seconds long, at most 20 seconds) and stretches of silence are skipped. Chunks are transcribed one at a time by the same Whisper backend as voice input, and each becomes a line such as `[03:12] ...` (time since the session started). **Stop** finishes the chunks still being transcribed before it says "Stopped".
- While the assistant is speaking a reply, the transcriber pauses so the assistant's voice is not transcribed.
- Drag the bar between the transcript and the summary to resize them, and the grip under the **System message** (collapsed by default) to resize it. **Ctrl+mouse wheel**, **Ctrl+plus/minus** or the **A** buttons change the text size. The transcript scrolls with new text only when you are already at the end.
- When stopped, the transcript can be edited (fix names before summarizing). **Summarize** writes a **Summary**, **Action items**, **Meeting notes** or **Key points** with the chat model, also while recording; press it again to cancel.
- **Copy** the transcript or summary, **Save...** it as Markdown or text, or **Send to chat**: the transcript and summary become the main chat's context and the message box starts with "Using the transcript, ". The chat also gets the transcript as context while it grows (its last 8,000 characters plus the summary).
- Each session is saved automatically when you stop, clear or close the window, to `%APPDATA%\VoiceChatbot\transcripts\transcript_yyyyMMdd_HHmmss.md`. **Open folder** shows them.
- Shortcuts in the window: **Ctrl+R** start/stop, **Ctrl+S** save, **Ctrl+0** default text size. **Esc** does nothing there, so it cannot stop a recording by accident.

## Conversations, memory and knowledge

### Saved conversations

Every chat is saved automatically, one JSON file per conversation in `%APPDATA%\VoiceChatbot\conversations`. Messages from the phone remote and scheduled prompts that appear in the main chat are saved too, along with attached image paths and the spoken-reply audio.

- Click the history button in the top bar (or press **Ctrl+H**) to open **Conversations**. Chats are grouped by Today, Yesterday, Previous 7 days and Older, and the search box matches titles and message text.
- Click a conversation to reopen it. Its messages are redrawn, Replay Audio comes back if the audio file still exists, and the last *Max context messages* are loaded back into the model's context.
- Hover a conversation to rename or delete it. Delete asks for confirmation.
- **New chat** (the + button or **Ctrl+N**) and **Clear Chat** start a fresh conversation. The previous one stays in history.
- Conversations are titled from the first message. Turn off **Save conversations** under **Chat History** in the sidebar to stop saving new messages. **Open Folder** shows the files.

### Memories

**Save Memory** in the **Conversation** expander summarizes the current chat into a memory, and **View Memory** lets you add, edit or delete them. Memories are stored in `%APPDATA%\VoiceChatbot\memory`.

**Memories in prompt** decides which memories go into the system prompt:

- `Relevant` (default) sends only the saved memories that best match your message, up to **Matching memories** (default 4), plus the newest memory. Matching is keyword based, so a message about "my dog Rex" brings back memories that mention Rex or dogs. A message with no real topic, or a question like "what do you remember?", gets the most recent memories instead.
- `All` sends every saved memory with every message, which uses more of the model's context.

The hint under the setting shows how many memories the last message used. The settings are saved as `MemoryMode` and `MemoryMaxItems` in `settings.json`.

### Knowledge folder

The assistant can answer from your own documents. Open **Knowledge Folder** in the sidebar, click **Browse** (or type a path and press Enter) and turn on **Use my documents**.

- PDF, Word (`.docx`), `.txt`, `.md`, `.csv`, `.json`, `.xml` and `.log` files are read, including subfolders. Files over 25 MB, hidden files and Office lock files are skipped. Scanned PDFs need the same OCR tools as attached documents (Poppler and Tesseract).
- Indexing runs in the background. The status line shows progress, then the number of files and chunks and when the folder was last indexed. Hover it to see files that could not be read. While indexing, the button reads **Stop**.
- While it is on, the folder is checked again a few seconds after the app starts and whenever you change it, and only new or changed files are read again. **Reindex** does the same now and also retries files that could not be read before (for example after installing OCR).
- For each message, desktop or phone remote, the best-matching passages (up to **Excerpts per message**, default 4, about 900 characters each) are added to that request only, and the chat shows a note such as *Using 3 excerpts from: lease.pdf, car.md*. Matching is keyword based, like memories: a message has to share its main words with a passage, so small talk and general questions are not affected. File and subfolder names count as words too.
- The index is stored in `%APPDATA%\VoiceChatbot\knowledge-index.json`; your documents are never changed. The settings are saved as `KnowledgeEnabled`, `KnowledgeFolder` and `KnowledgeMaxChunks` in `settings.json`.

### Personas

A persona is a named preset for the system prompt, the voice, the speech rate and, optionally, the model. Pick one from the **Persona** box in the top bar, left of the voice buttons: the system prompt, voice and speech rate switch to it, and so does the model if the persona has one.

- On first run a **Default** persona is made from your current prompt, voice and rate.
- Under **Personas** in the **Chat Backend** expander, type a name and click **Save as new persona** (or press Enter) to save the current prompt, voice and rate as a new persona. **Update persona** saves them into the active persona. Turn on **Include the current model** before saving or updating to make the persona also switch models; leave it off to keep whatever model is selected.
- **Delete persona** deletes the active persona after a second click to confirm, then switches to the first remaining one. The last persona cannot be deleted.
- Changes you make to the prompt, voice or speed are kept, but are not saved into the persona until you click **Update persona**.
- Saved conversations remember their persona. Reopening a conversation switches back to that persona if it still exists.
- Personas are saved as `Personas` and `ActivePersona` in `settings.json`.

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

Enable **Phone Remote** and choose a LAN port. The app creates a local certificate. Install and trust the generated `.cer` certificate on the phone, then open the displayed HTTPS URL while both devices are on the same network. The remote always needs a PIN: if none is set, a random 6-digit PIN is created when it starts and shown under **Phone Remote** (and in its status). Enter it once on the phone; the page remembers it. **New PIN** creates a different one.

### YouTube and document tools

The Windows installer bundles `yt-dlp`, Deno, and `ffmpeg`, so YouTube captions and audio fallback work on a clean installation. Release builds fetch current official Windows binaries from their projects.

Optional tools improve document support:

- Poppler `pdftoppm` and Tesseract OCR for scanned PDFs.

These tools can be installed with WinGet where packages are available.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| Enter or Shift+Enter | Send |
| Ctrl+Enter | New line |
| Ctrl+L | Listen for one question |
| Esc | Stop generating, speaking and listening |
| Ctrl+B | Show or hide the settings sidebar |
| Ctrl+K | Focus the message box |
| Ctrl+H | Show or hide saved conversations |
| Ctrl+N | Start a new chat |
| Ctrl+V | Paste text or an image |
| Ctrl+Alt+Space (any app) | Listen for one question; press again to stop listening |

### Tray and hotkey

The listen hotkey (Ctrl+Alt+Space by default) works in any app, also while Voice Chatbot is minimized or hidden in the tray. It starts listening like **Listen** does and stops listening when pressed again. Pick Ctrl+Shift+Space, Ctrl+Alt+L or Off under **App > Listen hotkey**; if another app already uses the combination, the app says so there and in the chat. Right-click the tray icon for **Open Voice Chatbot**, **Listen now**, **Speak responses** and **Exit**, or double-click it to open the window. Turn on **App > Minimize to tray** to hide the window in the tray instead of the taskbar when you minimize it.

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

- Settings, API keys, downloaded Whisper models, generated media, memories, saved conversations, and phone certificates are stored outside the installation directory under `%APPDATA%\VoiceChatbot`.
- Do not commit `settings.json`, certificates, passwords, API keys, model files, or private batch files.
- The SSH password, OpenAI API key, Tavily API key and phone remote PIN are encrypted in `settings.json` with Windows DPAPI for your Windows account (they appear as `"dpapi:..."`). Plain-text values from older versions are encrypted the next time the app starts. A settings file copied to another PC or Windows user cannot be decrypted there: those fields are left empty, the app warns once, and you enter them again. Still use a dedicated LAN account for SSH and restrict network access appropriately.
- SSH host keys are pinned on first use. The first connection to each SSH `host:port` trusts the server's key, saves its fingerprint in `settings.json` as `SHA256:...` (the same text `ssh-keygen -lf` prints for the server's host key) and posts a "Trusted SSH host key" note in the chat. After that, a server that presents a different key is refused with an error saying the host key changed. **Model Server Control > Host key** shows the pinned fingerprint. After you reinstall or reconfigure the SSH server, click **Forget host key** there and the next connection trusts the new key.
- The phone remote checks its PIN (in constant time) on every API request; spoken replies, generated images and videos are fetched by random, unguessable links. After 5 wrong PINs within 10 minutes, that IP address is locked out for 10 minutes: the phone shows "Too many wrong PIN attempts" (HTTP 429), even the right PIN is refused until the lockout ends, and the desktop app posts a note in the chat. A correct PIN resets the count, and **New PIN** lifts all lockouts. Requests without the right PIN are refused before their body is read, and request sizes are capped (64 MB for a voice clip, 256 MB for a message with files or a meeting recording). Uploaded files are saved under random names in `%TEMP%\VoiceChatbot\phone-*`, so a file name cannot place a file anywhere else.
- The app keeps a daily log in `%APPDATA%\VoiceChatbot\logs\app-YYYYMMDD.log` for 7 days: startup and shutdown, every system message shown in the chat, backend errors and crashes. Saved keys and passwords are masked in it. Use **App > Open logs folder** in the settings sidebar to attach it to a bug report.
- The listen hotkey turns the microphone on from any app, also while the window is hidden in the tray. Face gating still applies, the tray icon's tooltip shows "Listening...", and **App > Listen hotkey > Off** turns it off. It is registered with Windows only while Voice Chatbot runs.
- Uninstalling the application does not delete `%APPDATA%\VoiceChatbot`, so reinstalling preserves settings. Delete that folder manually to remove all local app data.
