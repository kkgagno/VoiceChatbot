# Voice Chatbot Mini

Voice Chatbot Mini is the standalone, smaller edition of Voice Chatbot: a Windows desktop and phone-browser voice assistant that works right after installing, without the extras that need the original home lab. It comes with its own AI model (Gemma 4 E4B on a bundled llama.cpp) and its own voice (Kokoro), so it runs offline with no setup. It can also use a bigger Gemma 4 model, Ollama, llama.cpp and other OpenAI-compatible servers, or OpenAI. It supports voice conversation, local Whisper transcription, image prompts (the built-in Gemma 4 models see pictures too), web search, a knowledge folder, a live transcriber and a phone remote.

## What Mini has, and what it leaves out

Mini keeps:

- Typed and spoken chat with the built-in model, Ollama, llama.cpp or any OpenAI-compatible server (OpenAI included), the **Choose AI model...** chooser, model picking and **Refresh Models**.
- Voice input with Whisper (**Listen** / **Stop listening**, wake word, global hotkey) and spoken replies with the built-in Kokoro or a remote Kokoro server.
- Images attached to a message for the built-in Gemma 4 models and other vision models (Image button, Ctrl+V, phone remote files), documents and OCR.
- The Live Transcriber (desktop window and web page), knowledge folder, Tavily web search, tools, personas, memories, saved conversations, the scheduler, the phone remote, tray icon, hotkey and themes.

Mini does not have:

- **Images & Video**: no ComfyUI image creation, image editing or video generation.
- **Face Presence**: no camera, face profiles or identity gating; voice input is always allowed.
- **Model Server Control**: no Hermes SSH commands and no buttons or commands that start, stop or switch models on other servers. Mini starts and stops only its own built-in llama.cpp server; start your own Ollama or llama.cpp server yourself, and Mini connects to it.

## Help inside the app

Click **Help** in the top bar (or press **F1**) to open the Help window. It walks through every sidebar section and top-bar control in plain language, with **Getting started**, **What you need** (graphics card guidance, Whisper), **Choose a chat model** (the Gemma 4 models and the video memory each needs, Ollama, llama.cpp, LM Studio and OpenAI), the Live Transcriber, the scheduler, the phone remote, and **Troubleshooting** (where the logs are and the common problems). Type in its search box to find a topic, and use **Next** to go through them in order.

- **F1** inside a settings section opens the topic for that section, and the topic's **Show this section** link opens that section in the sidebar. F1 also works in the Live Transcriber and the Scheduler.
- New chats show a **New here? Open Help** link under the suggestions.
- The help text lives in `Core/HelpContent.cs` (topics with paragraphs, lists and tips; `**bold**` for labels, `` `code` `` for things to type). Its unit tests check that every sidebar section has a topic and that links between topics resolve.

## Download and install

The installer is several GB because the AI model is inside it, so it comes in parts. From the GitHub release named **Voice Chatbot Mini**, download `VoiceChatbotMini-Setup-<version>-win-x64.exe` **and** all its `VoiceChatbotMini-Setup-<version>-win-x64-1.bin`, `-2.bin`, ... files into the same folder, then run the `.exe`. The parts must stay together in that folder while it installs. There is no portable ZIP.

The installer is not code-signed yet, so Windows SmartScreen may show an **Unknown publisher** warning. Verify the SHA-256 checksums published with the release (`SHA256SUMS.txt`) before running it.

The installer:

- Installs a self-contained Windows x64 build to `%LOCALAPPDATA%\Programs\VoiceChatbotMini`. The .NET runtime is included.
- Includes everything needed to chat and talk offline: llama.cpp's `llama-server` (Vulkan build, in `llama\`), the **Gemma 4 E4B** model (Q4_K_M, in `models\`, with its picture support `gemma-4-E4B-it-mmproj.gguf` so it can look at pictures) and **Kokoro** speech (KokoroSharp with the Kokoro 82M model and voices, in `kokoro\`, run by ONNX Runtime with DirectML on the graphics card or on the processor). No Python, Ollama or other server is needed.
- Creates **Voice Chatbot Mini** Start Menu shortcuts and, optionally, a desktop shortcut.
- Leaves settings and conversation data in `%APPDATA%\VoiceChatbotMini`, and models you download later in `%LOCALAPPDATA%\VoiceChatbotMini\models`.

Windows 10 version 1809 or newer, or Windows 11, is required. A graphics card with 8 GB of video memory or more (NVIDIA, AMD or Intel) makes the included model fast; without one it runs on the processor, more slowly.

### Next to the full Voice Chatbot app

Mini is a separate product: its own installer ID, install folder, `VoiceChatbotMini.exe`, Start Menu entries, data folder (`%APPDATA%\VoiceChatbotMini`) and temp folder (`%TEMP%\VoiceChatbotMini`). Installing, updating or uninstalling one never touches the other.

- **Settings on first run.** When Mini starts for the first time and finds the full app's `%APPDATA%\VoiceChatbot\settings.json`, it copies just that file, so your backend, voice, personas and keys carry over (the keys still decrypt for the same Windows user). The full app's settings for features Mini does not have are ignored. Chats, memories, Whisper models, transcripts and logs are not copied; Mini keeps its own. The copy is noted once in the chat and in the log.
- **Both running at once.** Mini's built-in Kokoro runs inside the app and needs no port, and its built-in llama.cpp server listens on `127.0.0.1` on a free port it picks itself, so neither clashes with the full app. The phone remote defaults to port `5100` in both apps: if the full app's remote is running, Mini says the port is already in use; enter another port under **Phone Remote** (for example `5101`). Only one app can own a global listen hotkey at a time; the second says so under **App > Listen hotkey**.

## Choosing a chat model

Nothing has to be set up: the default **Provider** (Chat Backend) is `Built-in model`, which runs the included Gemma 4 E4B on this PC. The app starts its bundled `llama-server` on `127.0.0.1` by itself, loads the model and stops it when you switch to another provider or close the app. Chat Backend shows its state ("Gemma 4 E4B (default): loading...", "ready on <GPU> (16K context, sees pictures)" or "could not start (reason)") with a **Restart** button; "(default)" marks the model that came with the app. If the graphics card cannot hold the model, it runs on the processor (slower) and the chat says so. The server's log is `%APPDATA%\VoiceChatbotMini\logs\llama-server.log`.

The built-in Gemma 4 models see pictures: the app starts `llama-server` with the model's vision projector (`--mmproj`, the model's `-mmproj.gguf` file, called *picture support* in the app) when it is on this PC. Picture support comes with the included model and is part of each download. A model downloaded without it (with an older version of the app) reads text only, and Chat Backend says "text only": pick it in **Choose AI model...** and click **Add picture support**, which downloads just that file. If the picture support file does not load, the model starts text only anyway and the chat says so. `llama-server` reads JPEG, PNG, GIF and BMP pictures, so the app converts others (WebP, HEIC, AVIF, TIFF) to JPEG with Windows before sending them; one Windows cannot open is left out, with a note in the chat. A picture sent to the built-in model while it cannot see pictures is left out of the request, with a note in the chat, instead of failing the message; a server that rejects pictures gets a plain "this model can't see pictures" message too.

### The model chooser

**Choose AI model...** at the top of Chat Backend opens the model chooser; it also opens by itself on the first start (**Skip for now** keeps the included model). It shows this PC's graphics card, video memory and memory, and gives each Gemma 4 model a badge: *Fits your graphics card*, *Part runs on the processor: slower*, *Runs on the processor: slow* or *Too big for this PC*. The default model, Gemma 4 E4B (the one included with the app, used unless you choose another), stands out with a filled **Default** badge with a star and the line "The default model: ready right away, no download needed."; its button reads **Use the default model**.

| Model | Download | Video memory | Graphics card |
| --- | --- | --- | --- |
| Gemma 4 E4B | Included | about 6.5 GB | 8 GB or more: RTX 3060 Ti, RTX 4060, RX 7600, Arc A750 (6 GB cards work with a little on the processor) |
| Gemma 4 12B | about 8.5 GB | about 9.5 GB | 12 GB: RTX 3060 12 GB, RTX 4070, RTX 5070, RX 6700 XT, Arc B580 |
| Gemma 4 26B A4B (mixture of experts: close to 31B quality, but quick) | about 18 GB | about 19 GB | 24 GB: RTX 3090/4090/5090, RX 7900 XTX (16 GB cards run it with part on the processor) |
| Gemma 4 31B (the smartest, slower) | about 19.5 GB | about 21 GB | 24 GB: RTX 3090/4090/5090, RX 7900 XTX |

The chooser also offers models that need no video memory on this PC:

- **Ollama**: an address such as `http://localhost:11434`. Needs [Ollama](https://ollama.com) with a model pulled. Free.
- **llama.cpp server (or another OpenAI-compatible server)**: LM Studio, vLLM and similar; an address such as `http://192.168.1.50:8080/v1`, plus an API key only if the server has one. Free.
- **OpenAI (cloud)**: needs an [OpenAI API key](https://platform.openai.com/api-keys) with billing set up. Pay per use (`gpt-5-mini` by default, a fraction of a cent per answer; see [prices](https://openai.com/api/pricing)). Messages are sent to OpenAI.

Downloads come from Hugging Face (Q4_K_M GGUF files, plus the model's picture support file from the same repository, f16 when there is one; the sizes above include it) and run in the background, with one progress bar in the chooser and in Chat Backend. Each card's **Pictures** line says whether the model can look at pictures. **Pause** stops a download; starting it again continues where it stopped. Each file's SHA-256 checksum is verified when it is complete. While a chosen model downloads the app keeps using Gemma 4 E4B, and switches to the new model by itself when it is done. Downloaded models are kept in `%LOCALAPPDATA%\VoiceChatbotMini\models` (the chooser's **Models folder** button opens it); delete files there to free disk space. Uninstalling does not delete them. The included E4B lives in the app folder's `models` folder.

With `Built-in model` selected, the Ollama URL, OpenAI URL, API key, Model and Refresh Models fields are hidden. With `Ollama` only the Ollama URL shows; with `OpenAI-compatible` the URL and key show.

### Your own Ollama server

1. Install [Ollama for Windows](https://ollama.com/download/windows).
2. Pull a model, for example:

   ```powershell
   ollama pull gemma4
   ```

3. In Voice Chatbot Mini click **Choose AI model...**, pick **Ollama**, enter `http://localhost:11434` and click **Use Ollama** (or select `Ollama` under **Provider** and set the Ollama URL).
4. Click **Refresh Models** and select the model.

### Your own llama.cpp or another OpenAI-compatible server

1. Install or build [llama.cpp](https://github.com/ggml-org/llama.cpp).
2. Start `llama-server.exe` with your GGUF model.
3. For image-capable models, use the matching multimodal projector or media embedder required by that model.
4. In Voice Chatbot Mini click **Choose AI model...** and pick **llama.cpp server (or another OpenAI-compatible server)**, or select `OpenAI-compatible` under **Provider**.
5. Set the URL to the server's `/v1` endpoint, for example `http://192.168.1.50:8080/v1`.
6. Click **Refresh Models**.

**Context window.** The app asks the server how many tokens one request can use and plans the chat history to fit: llama.cpp's per-slot `n_ctx` (from `/props`, or `/slots`), vLLM's `max_model_len` or LM Studio's loaded context length. The model's training context (`n_ctx_train`) is never used, and nothing is guessed from the model name for a local or self-hosted server. The line under **Context window** (Chat Backend) shows the value in use and where it came from, for example `llama.cpp server: 16,384 tokens per request (detected)`. On llama.cpp the window is set with `-c` / `--ctx-size` on `llama-server`; with `-np` parallel slots each request gets one slot's share. The value is checked again every five minutes, after **Refresh Models** and when the URL or model changes, so a restarted server with a new `-c` is picked up without restarting the app. When the server reports nothing, the **Context window** box is used. If the server still rejects a request as too long, the app reads the window again, leaves out older messages and sends it once more; if that fails too, it tells you the server's size. With Ollama the box is sent as `num_ctx`, capped at the model's maximum. The built-in model is started with the box's value as its `-c` (16384 by default; `0` means as much as the graphics card has room for next to the model), and changing it restarts the built-in model.

Mini starts and stops only its built-in model. It never starts, stops or switches models on servers you run yourself: start `llama-server` (or Ollama) yourself, then click **Refresh Models**.

### Tools and formatting

With **Let the model use tools** on (Chat Backend, on by default), the model decides by itself when to search the web (needs Web Search on and a Tavily key), check the current date and time, get a stock quote, read a web page, or save a memory. A short note appears in the chat for each tool it uses. So that a web page cannot plant memories, the model cannot save a memory in an answer where it has already read web results; ask it to remember things in a separate message. The Pi command ("ask pi ...") works as before, and "search the web for ..." or "look it up online" still forces a search.

Tools need a model and server with function calling: in Ollama, a model tagged *tools* (for example `qwen3` or `llama3.1`); in llama.cpp, start `llama-server` with `--jinja` (the built-in server is started with it). If the model cannot use tools, the app shows one note, turns tools off for that model until restart and answers normally. The phone remote and scheduled prompts do not use tools yet.

Finished replies are shown formatted: headings, bold, italic and strikethrough text, inline code, bullet, numbered and task lists, quotes, rules, tables and links (links open in your browser; only `http`, `https` and `mailto`). Code blocks keep their **Copy** button. While a reply streams it shows as plain text, and formatting is never read aloud. The phone remote and the scheduler window show plain text. Turn off **Format replies (Markdown)** in Chat Backend to get plain text in the chat too. The default system prompt now allows light Markdown; an unchanged old default is updated automatically, but if you wrote your own prompt, remove any "no Markdown" instruction from it to get formatted replies.

### Hide model thinking

**Hide model thinking** (Chat Backend, on by default, saved as `DisableModelThinking`) asks the server to skip the model's thinking phase, so replies start sooner:

- llama.cpp and other OpenAI-compatible servers get `"chat_template_kwargs": {"enable_thinking": false}` and `"reasoning_format": "deepseek"`; thinking a model still writes then arrives separately and is ignored. These fields are not sent to `api.openai.com`.
- Ollama gets `"think": false`.
- A server that answers HTTP 400 to these fields gets the request again without them, and is then sent requests without them until the app restarts.
- The built-in model's `llama-server` is also started with `--reasoning off` when its llama.cpp build has that option (checked in `llama-server --help`). Gemma 4 can still think with `enable_thinking: false` alone; turning thinking off at the server makes its replies much faster. Changing the switch restarts the built-in model. If llama.cpp refuses the option, the model is started once more without it, and the log says so.

Some local models write plain-text planning notes before (or instead of) their answer, such as `The user said "hi". Wait, they might want more. Let's try: "Hi!"`. These are never shown, saved or spoken, whether the switch is on or off: while the reply streams the bubble shows "Thinking...", and the finished bubble keeps only the answer (the text after the notes, after "Final answer:", "Response:" or "Reply:", or the quoted reply after "Let's try:"). When there is no answer at all, the bubble says so and nothing is spoken. The hidden notes are written to the app log. `<think>...</think>` blocks are removed as before.

## Voice requirements

### Speech input

Local transcription uses Whisper.net. Use **Download Model** inside the app to download a Whisper model. A microphone is required for voice input.

Whisper can run on the graphics card: the installer includes the Vulkan build of Whisper.net, which works on AMD Radeon (including Ryzen integrated graphics), NVIDIA and Intel GPUs with a current driver. Turn on **Use GPU for speech recognition** under **Voice Input** (off by default, saved as `WhisperUseGpu`) to try it. It can be faster, but on some AMD integrated GPUs it repeats or drops words (every sentence arriving twice, or no reply at all); turn it off if that happens. Without a usable Vulkan GPU, or if it fails to load, Whisper falls back to the CPU automatically. The line under the switch shows what the model runs on, for example `Whisper: Vulkan GPU (AMD Radeon(TM) 780M)` or `Whisper: CPU`. Changing it reloads the model; switching to the GPU after starting with it off needs a restart. Updating from 1.0.16, which had it on by default, turns it off once.

Each utterance is transcribed on its own, without the previous one as context, and a sentence Whisper repeats back to back ("This is bullshit.This is bullshit.") is sent once.

**Listen** turns the microphone on and keeps it on, so you can talk hands-free: say something, get the answer, say the next thing. The button then reads **Stop listening**; press it (or Ctrl+L, or Esc / **Stop**) to turn the microphone off.

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

Kokoro is built in: the installer includes KokoroSharp with the Kokoro 82M model and its voices (in the app folder's `kokoro\` folder). It works offline and needs no Python or server. The old Python setup (`install-kokoro.ps1`) is no longer needed.

It runs on the graphics card or on the processor, whichever is faster on this PC. The app ships ONNX Runtime's DirectML build (`onnxruntime.dll` and `DirectML.dll`), which works on any DirectX 12 graphics card (NVIDIA, AMD, Intel). When the app starts with the built-in Kokoro in use, it loads Kokoro in the background on both, speaks one untimed sentence on each, times the same test sentence on each and keeps the faster; the log says what it measured, for example `Built-in Kokoro: graphics card 0.4 s, processor 1.6 s for the test sentence; using the graphics card.` The graphics card is only used when its audio looks right (not empty, silent or broken, and about as long as the processor's); if it fails later, the processor takes over by itself. A reply that is ready before this is done waits for it instead of loading Kokoro a second time. The SPEECH line in the sidebar shows `Built-in Kokoro (graphics card)` or `Built-in Kokoro (processor)`.

#### Remote Kokoro server

To use Kokoro running on another machine (for example [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI)), open **Voice Output** in the settings sidebar and enter its address in **Remote Kokoro host**. Any of these forms work:

- `192.168.1.50` (port `8880` is assumed)
- `192.168.1.50:8880`
- `http://tts-box:8880/v1` or the full `.../v1/audio/speech` URL

Click **Test** to check the connection. If the server lists its voices, the voice dropdown is refreshed with them. **Engine** controls the fallback:

- `Auto` tries the remote host first and falls back to the built-in Kokoro. After a failure it skips the remote host for 60 seconds so replies are not delayed.
- `Remote only` never uses the built-in Kokoro.
- `Local only` ignores the remote host.

Leave the host blank to use only the built-in Kokoro. The host is saved as `KokoroRemoteUrl` in `settings.json`.

### Speaking replies

With the built-in Kokoro, a finished reply is spoken in pieces so the voice starts sooner: the first piece is usually the first sentence (at least about 60 characters, because Kokoro garbles very short clips such as "Sure!"), and the next, longer pieces (about 220, then 350 characters and up) are made while the one before plays. A remote Kokoro, a reply with code, and a short reply are spoken in one go: the whole reply goes to Kokoro in one request and then plays. The **Replay Audio** and **Download Audio** buttons are for the whole reply: they appear once a reply spoken in pieces has finished playing or was stopped (joined into one file; after a stop the rest is made in the background first), or when a reply spoken in one go starts playing. **Stop** (or **Esc**) and the microphone button stop speech straight away, also while the audio is still being prepared; a reply that was stopped or cancelled is not spoken. After the reply, hands-free listening and the wake word resume as usual. Code blocks are never read aloud. (The **Interrupt by speaking** option has been removed; its old settings are ignored.)

To have the assistant start talking while the model is still writing, turn on **Start speaking before the reply finishes** under **Voice Output** (off by default, needs **Stream responses**, saved as `StreamingSpeechEnabled`). The reply is then spoken in parts of at least about 120 characters, so Kokoro never gets a single word or short sentence on its own, and the next part is rendered while the current one plays. Replies to code or script requests still wait until they are complete. With this switch on, the Replay/Download buttons appear once the whole reply has been spoken. Updating turns the switch off once, because the earlier sentence-by-sentence speech sounded garbled; turn it back on if you prefer it.

### Wake word ("Hey Onyx")

Turn on **Only respond after the wake word** under **Voice Input** to have the app keep listening but answer only when you address it. Turning it on also turns on **Listen** (the microphone stays on), and it starts waiting again when the app opens. The status shows `Waiting for "hey onyx"`.

- Say the wake word and your request together: "Hey Onyx, what's the weather?" sends "what's the weather?".
- Or say just "Hey Onyx", wait for the Windows "Asterisk" sound, then ask within about 8 seconds. That one request needs no wake word.
- Everything else is ignored: no chat message and no reply (it is written to the log), and the app keeps listening. After each reply it waits for the wake word again.
- While the wake word is on, **Listen** (and the global hotkey and the tray's Listen item) keeps the microphone on and waits for the wake word; **Stop listening** turns the microphone off.
- Change the phrase in the **Wake word** box (default `hey onyx`). It is matched in the Whisper transcript, ignoring case and punctuation, with the usual spellings of "hey" (hay, hi, hei, a) and small spelling differences in the name ("Hey, Onix.", "Hey Annex", "Hey on X"), but not other names such as "Hey Annie".
- Saved as `AutoDetectVoice` (false when the switch is on) and `WakeWord` in `settings.json`. The old default "hey assistant" becomes "hey onyx"; a phrase you typed is kept. The openWakeWord detector ("Hey Jarvis") from 1.0.16 has been removed.

### Live Transcriber

**Transcribe** (next to **Listen**) opens the Live Transcriber, a separate window for meetings, calls and videos. It remembers its size, position, pane heights and text size (saved as `Transcriber` in `settings.json`).

- **Source**: **Microphone** (the microphone selected under **Voice Input**) or **PC audio** (everything the PC plays, recorded from the default speakers or headphones). Switching while recording carries on with the new source.
- Audio is cut into chunks at natural pauses (half a second of silence once a chunk is 2 seconds long, at most 20 seconds) and stretches of silence are skipped. Chunks are transcribed one at a time by the same Whisper backend as voice input, and each becomes a line such as `[03:12] ...` (time since the session started). **Stop** finishes the chunks still being transcribed before it says "Stopped".
- While the assistant is speaking a reply, the transcriber pauses so the assistant's voice is not transcribed.
- Drag the bar between the transcript and the notes to resize them, and the grip under the **System message** (collapsed by default) to resize it. **Ctrl+mouse wheel**, **Ctrl+plus/minus** or the **A** buttons change the text size. The transcript scrolls with new text only when you are already at the end.
- **Notes** are plain text in this layout (the web transcriber uses the same one):

  ```
  SUMMARY SO FAR            (after Stop or Re-summarize all: SUMMARY, ACTION ITEMS, MEETING NOTES or KEY POINTS)
  <short summary>

  NOTES BY TIME
  [00:00–05:12]
  - detailed bullet ...
  [05:12–10:20]
  - ...
  ```

  Each section holds bullets on every topic, fact, decision, action item (with owner and due date when said), question, name, date and number from that stretch only. Sections are never rewritten, so edits you make to them (when stopped) are kept; the summary at the top is rewritten each time. Notes typed without this layout are kept as they are on the first update.
- **Live notes** (with a 5, 10 or 15 minute interval; a stored 2 becomes 5) adds a section while recording. Every 15 seconds the window checks whether the interval has passed and at least 40 new words were said; then the chat model writes notes on only the text since the last update (a section from its first `[mm:ss]` to now), and a second request rewrites the short "SUMMARY SO FAR" from all section notes in the chosen style (if that fails, the new section is kept with the old summary). The header shows "Notes updated 2:41 PM". The notes pane is read-only while recording. Saved as `LiveNotes` and `LiveNotesIntervalMinutes` under `Transcriber`.
- **Stop** (with Live notes on, or when the notes already have sections) waits for the last chunks, adds a final section for the remaining words, then replaces the top with a full summary in the chosen style, written from the whole transcript (up to about 24,000 characters) or from the section notes (longer ones), and saves the session file again.
- The button next to the style picker reads **Update notes now** while recording (adds a section right away, whatever the interval), **Summarize** when stopped with empty notes, and **Re-summarize all** otherwise. Re-summarize all asks first, then rebuilds the notes from the whole transcript: one section per interval by the `[mm:ss]` times (a transcript without times is split by size into Part 1, Part 2...), then the full summary. The status shows "Section 3 of 12..." and "Writing the summary..."; press the button again to cancel, and the previous notes stay on cancel or error. When stopped, the transcript can be edited, so you can fix names before summarizing.
- Summary requests use the same context window as the chat (sent to Ollama as `num_ctx`), so long requests are not cut short.
- **Copy** the transcript or notes, **Save...** it as Markdown or text, or **Send to chat**: the transcript and notes become the main chat's context and the message box starts with "Using the transcript, ". The chat also gets the transcript as context while it grows (its last 8,000 characters plus the notes).
- Each session is saved automatically when you stop, clear or close the window, to `%APPDATA%\VoiceChatbotMini\transcripts\transcript_yyyyMMdd_HHmmss.md`. **Open folder** shows them.
- The current session (transcript, notes, recording time, live-notes progress and its file name) is also kept in `%APPDATA%\VoiceChatbotMini\transcripts\current-session.json` (about 2 seconds after each change, and on stop, close and exit). Opening the window again restores it ("Restored your last session from ..."); recording continues its timestamps and updates the same session file. **Clear** saves the session and starts a new one. An unreadable state file is ignored (and logged).
- Shortcuts in the window: **Ctrl+R** start/stop, **Ctrl+S** save, **Ctrl+0** default text size. **Esc** does nothing there, so it cannot stop a recording by accident.

## Conversations, memory and knowledge

### Saved conversations

Every chat is saved automatically, one JSON file per conversation in `%APPDATA%\VoiceChatbotMini\conversations`. Messages from the phone remote and scheduled prompts that appear in the main chat are saved too, along with attached image paths and the spoken-reply audio.

- Click the history button in the top bar (or press **Ctrl+H**) to open **Conversations**. Chats are grouped by Today, Yesterday, Previous 7 days and Older, and the search box matches titles and message text.
- Click a conversation to reopen it. Its messages are redrawn, Replay Audio comes back if the audio file still exists, and the last *Max context messages* are loaded back into the model's context.
- Hover a conversation to rename or delete it. Delete asks for confirmation.
- **New chat** (the + button or **Ctrl+N**) and **Clear Chat** start a fresh conversation. The previous one stays in history.
- Conversations are titled from the first message. Turn off **Save conversations** under **Chat History** in the sidebar to stop saving new messages. **Open Folder** shows the files.

### Memories

**Save Memory** in the **Conversation** expander summarizes the current chat into a memory, and **View Memory** lets you add, edit or delete them. Memories are stored in `%APPDATA%\VoiceChatbotMini\memory`.

**Memories in prompt** decides which memories go into the system prompt:

- `Relevant` (default) sends only the saved memories that best match your message, up to **Matching memories** (default 4), plus the newest memory. Matching is keyword based, so a message about "my dog Rex" brings back memories that mention Rex or dogs. A message with no real topic, or a question like "what do you remember?", gets the most recent memories instead.
- `All` sends every saved memory with every message, which uses more of the model's context.

The hint under the setting shows how many memories the last message used. The settings are saved as `MemoryMode` and `MemoryMaxItems` in `settings.json`.

### Knowledge folder

The assistant can answer from your own documents. Open **Knowledge Folder** in the sidebar, click **Browse** (or type a path and press Enter) and turn on **Use my documents**.

- PDFs (scanned ones too), Word, Excel, PowerPoint, OpenDocument, RTF, text, Markdown, CSV, JSON, XML, logs, web pages, emails (`.eml`, Outlook `.msg`) and pictures are read, including subfolders (see [Documents and OCR](#documents-and-ocr)). Files over 25 MB, hidden files and Office lock files are skipped.
- Indexing runs in the background. The status line shows progress (*Reading 3 of 25: HOA Bylaws.pdf*, with OCR page counts for scans), then the number of files and chunks, when the folder was last checked, and what was left out, e.g. *1 could not be read · 40 pictures without text · 5 skipped: 3 video, 2 archive*. Hover it for the reasons. While indexing, the button reads **Stop**.
- **Files** opens a list of every file in the folder with its status (*Indexed - 12 chunks*, *Could not read - no text*, *No text - no words in the picture*, *Skipped - type not supported*, *Too large*), sortable by name, type or status, with **Open folder**. Double-click a file to show it in Explorer. Select an indexed file and click **View text** to see exactly what the assistant reads from it, in a monospace window with **Copy**: the quickest way to check how a form or table came out.
- PDFs are read the way the page looks: each line is one row of the page, left to right, so a form's label and its amount stay together even when the PDF draws them far apart (*11 Subtract line 10 from line 9. This is your adjusted gross income ... 11 | 112,258*). ` | ` marks a wide gap between columns and dot leaders become `...`. Values typed into a fillable PDF form (text boxes, ticked boxes as `[X]`, chosen options) are read too and placed where their fields are. PDFs indexed by an earlier version of the app are read again automatically the next time the folder is checked.
- While it is on, the folder is checked again a few seconds after the app starts and whenever you change it, and only new or changed files are read again. **Reindex** does the same now and also retries files that could not be read before (for example after adding an OCR language) and reads the rest of scans indexed when OCR stopped at 8 pages; photos and empty files that simply have no text are not read again until they change. A reindex you start (Reindex, a new folder, turning the switch on) always ends with a one-line summary in the chat, such as *Knowledge folder: 18 files indexed (412 chunks), 2 new or changed. Skipped 5 (3 video, 2 archive: not supported). Could not read 1: lease.pdf (this PDF file is password-protected). No text found in 40 image files.* or *Knowledge folder is up to date: 18 files (412 chunks), checked 11:52 PM.* The background checks only post when a file could not be read (a photo without words does not count).
- A message about your documents (a passage matches it, or it mentions your documents, files, the folder or a word from a file name such as *deed* or *insurance*), desktop or phone remote, tells the model the folder's name and which documents are in it (up to 60 names), with an instruction to say which document might have an answer rather than invent its contents and to ignore the documents if the message is really about something else. When an answer uses a number, amount, date or name from a document, the model is asked to quote the exact line it came from and name the file, to say it is unclear rather than guess when a form's label and value are not clearly on the same line, and, when you say an answer is wrong, to re-read the text and quote it instead of guessing again. The folder's whole text is added when it fits in about a third of the model's context window; otherwise the best-matching passages are added, at least **Excerpts per message** (default 4, about 900 characters each) and more while they match well and fit. The chat shows a short note such as *Using 3 excerpts from: lease.pdf, car.md* or *Using all 5 documents in 'townhouse'*. Matching is keyword based, like memories, so small talk gets nothing from the folder. If you turn **Use my documents** off in a chat that already used them, the model is told not to bring them up again unless you ask; a new chat starts clean. File and subfolder names count as words and lift that file's passages, and a short follow-up (*and the parking rules?*) also uses your previous question to rank them.
- The index is stored in `%APPDATA%\VoiceChatbotMini\knowledge-index.json`; your documents are never changed. The settings are saved as `KnowledgeEnabled`, `KnowledgeFolder` and `KnowledgeMaxChunks` in `settings.json`.

### Documents and OCR

Attached documents and the knowledge folder use the same reader (`DocumentTextService`):

| Type | Read as |
| --- | --- |
| PDF | Text of each page, line by line as it looks on the page (` \| ` between columns), with the values of a filled-in form. Pages without a text layer (scans) are read with OCR, up to 8 pages per file (200 in the knowledge folder); the reply notes which pages were left out. |
| Word `.docx`, `.doc`, `.rtf`, `.odt` | Body text, headers, footers and footnotes. Old `.doc` files (Word 97-2003) are read directly. |
| Excel `.xlsx`, `.xlsm`, `.ods` | Each sheet as `Sheet: name`, then one line per row with comma-separated cells (dates as `yyyy-mm-dd`). |
| PowerPoint `.pptx`, `.odp` | `Slide n` with the slide text and speaker notes. |
| Emails `.eml`, `.msg`, web pages `.html`, `.mht` | From, To, Date and Subject, then the message text; attachments are named but not read. |
| Pictures `.png`, `.jpg`, `.gif`, `.bmp`, `.tif`, `.webp`, `.heic` | Text in the picture, with OCR. Photos without words show as "No text was found in this picture". |
| Text, Markdown, CSV, JSON, XML, logs, scripts | As they are (UTF-8, UTF-16 or Windows ANSI). |
| `.xls`, `.ppt`, Publisher, Visio, WordPerfect and other types | Through the Windows text filter (IFilter) for that type, when one is installed (Windows includes one for old Office files; Office and the Microsoft Office filter pack add more). Otherwise the file is listed with what to do, such as saving it as `.xlsx`. |

- **OCR** uses the text recognition built into Windows 10 and 11, in your Windows display language (else English). Nothing has to be installed; if Windows has no OCR language, add one in **Settings > Time & language > Language** (**Language & region** on Windows 11). When Windows OCR is not available, Poppler `pdftoppm` and Tesseract are used if they are installed.
- **`.heic` photos** (iPhone) need **HEIF Image Extensions** and **HEVC Video Extensions** from the Microsoft Store; **`.webp`** needs **Webp Image Extensions** (installed on most PCs).
- **OneDrive**: online-only files are downloaded when they are read. If that fails (OneDrive is signed out or offline), the file is listed as online-only: right-click the folder in File Explorer and choose **Always keep on this device**.
- Password-protected files cannot be read; remove the password and save the file again.

### Personas

A persona is a named preset for the system prompt, the voice, the speech rate, the temperature and, optionally, the model. Pick one from the **Persona** box in the top bar, left of the voice buttons: the system prompt, voice, speech rate and temperature switch to it, and so does the model if the persona has one. A persona saved before temperature was part of personas keeps the current temperature until you click **Update persona** on it once.

- On first run a **Default** persona is made from your current prompt, voice, rate and temperature.
- Under **Personas** in the **Chat Backend** expander, type a name and click **Save as new persona** (or press Enter) to save the current prompt, voice, rate and temperature as a new persona. **Update persona** saves them into the active persona. Turn on **Include the current model** before saving or updating to make the persona also switch models; leave it off to keep whatever model is selected.
- **Delete persona** deletes the active persona after a second click to confirm, then switches to the first remaining one. The last persona cannot be deleted.
- Changes you make to the prompt, voice or speed are kept, but are not saved into the persona until you click **Update persona**.
- Saved conversations remember their persona. Reopening a conversation switches back to that persona if it still exists.
- Personas are saved as `Personas` and `ActivePersona` in `settings.json`.

## Optional integrations

### Web search

Create a key at [Tavily](https://tavily.com), enter it in the app, and enable **Web Search**. API keys are stored locally in `%APPDATA%\VoiceChatbotMini\settings.json`; they are not included in the installer or repository.

### Phone HTTPS remote

Enable **Phone Remote** and choose a LAN port (default `5100`; pick another one, such as `5101`, while the full Voice Chatbot app's remote runs). The app creates a local certificate, named "VoiceChatbot Mini Local Remote" so it can be told apart from the full app's. Install and trust the generated `.cer` certificate on the phone, then open the displayed HTTPS URL while both devices are on the same network. The remote always needs a PIN: if none is set, a random 6-digit PIN is created when it starts and shown under **Phone Remote** (and in its status). Enter it once on the phone; the page remembers it. **New PIN** creates a different one.

#### Web transcriber (from another PC or a phone)

The remote also serves the Live Transcriber as a web page: open `https://<PC address>:<port>/transcribe` (or tap **Transcribe** at the top of the remote) in Chrome, Edge, Safari or Firefox on any device on the same network. Transcription and notes run on the PC with the same Whisper backend, chat model and prompts as the desktop window.

- **Same network.** The other device must reach the PC's LAN address; Windows may ask to allow the app through the firewall the first time the remote starts. Away from home, use a VPN to your home network such as [Tailscale](https://tailscale.com) and open the PC's Tailscale address instead (the certificate does not list that address, so the browser warns about it).
- **Certificate warning.** The remote uses its own self-signed certificate, so the first visit shows a privacy warning. Choose **Advanced > Continue** (or install the `.cer` from **Phone Remote > Certificate**). The microphone only works on the `https://` address.
- **PIN.** The page asks for the remote's PIN and remembers it (it shares the PIN with the main remote page). A wrong PIN shows the prompt again; chunks recorded meanwhile wait and are sent once the PIN is accepted.
- **Source.** **Microphone** (echo cancellation off, noise suppression and automatic gain on) or, in Chrome and Edge on a PC, **Tab / system audio**: pick a tab (or the entire screen) and turn on **Share tab audio** (or **Share system audio**). Only the sound is used.
- Audio is cut at pauses like the desktop window (half a second of silence once a chunk is 2 seconds long, at most 20 seconds, silence skipped). Chunks go to the PC one at a time and come back as `[mm:ss]` lines (time since the session started). **Stop** shows "Finishing..." until the last chunks are transcribed, then saves the session on the PC.
- **Notes** work as in the desktop window, in the same layout (SUMMARY SO FAR, then NOTES BY TIME with one `[mm:ss–mm:ss]` section per update): **Live notes** (5, 10 or 15 minutes; a stored 2 becomes 5) adds a section while recording, **Update notes now** adds one right away, **Stop** adds the last section and replaces the top with a full summary in the chosen style, and **Summarize** / **Re-summarize all** (asks first) rebuild the notes from the whole transcript ("Section 3 of 12...", "Writing the summary..."; press the button again to cancel, the previous notes stay). The notes are read-only while recording. The PC writes them as background jobs that the page checks every second, so a phone that sleeps for a moment does not lose them. They use the desktop transcriber's system message and the chat's context window.
- **Copy transcript**, **Copy notes**, **Download .md** (to the device), **Save on PC** (`%APPDATA%\VoiceChatbotMini\transcripts`, under a name the PC chooses; also saved automatically on Stop and before Clear) and **Send to chat** (the transcript and notes become the context of the desktop chat and the phone remote chat).
- Drag the bar between the transcript and the notes to resize them; **A-**/**A+** change the text size. Text size, pane split, source, style and live-notes choices are remembered in that browser. The transcript and notes can be edited when stopped. The browser also keeps the session (transcript, notes, recording time and live-notes progress): after a reload the page says "Restored your last session from ...", recording continues its timestamps, and **Clear** starts a new one.
- **Phones** stop the microphone when the screen locks or you switch apps. The page keeps the screen awake while recording where the browser allows it; keep the page open in front.

### YouTube and document tools

The Windows installer bundles `yt-dlp`, Deno, and `ffmpeg`, so YouTube captions and audio fallback work on a clean installation. Release builds fetch current official Windows binaries from their projects.

Optional tools for documents (see [Documents and OCR](#documents-and-ocr)):

- Poppler `pdftoppm` and Tesseract OCR, used for scanned PDFs only when Windows OCR has no language installed.
- The Microsoft Office filter pack, for old Office, Publisher and Visio files that Windows has no text filter for.

These tools can be installed with WinGet where packages are available.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| F1 | Help (inside a settings section: the help for that section) |
| Enter or Shift+Enter | Send |
| Ctrl+Enter | New line |
| Ctrl+L | Listen / Stop listening |
| Esc | Stop generating, speaking and listening |
| Ctrl+B | Show or hide the settings sidebar |
| Ctrl+K | Focus the message box |
| Ctrl+H | Show or hide saved conversations |
| Ctrl+N | Start a new chat |
| Ctrl+V | Paste text or an image |
| Ctrl+Alt+Space (any app) | Listen / Stop listening |

### Tray and hotkey

The listen hotkey (Ctrl+Alt+Space by default) works in any app, also while Voice Chatbot Mini is minimized or hidden in the tray. It works like the **Listen** / **Stop listening** button: press it to turn the microphone on, press it again to turn it off. Pick Ctrl+Shift+Space, Ctrl+Alt+L or Off under **App > Listen hotkey**; if another app already uses the combination, the app says so there and in the chat. Right-click the tray icon for **Open Voice Chatbot Mini**, **Listen** / **Stop listening**, **Speak responses** and **Exit**, or double-click it to open the window. Turn on **App > Minimize to tray** to hide the window in the tray instead of the taskbar when you minimize it.

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

Build the installer:

```powershell
winget install --id JRSoftware.InnoSetup -e
.\scripts\Build-Installer.ps1 -Version 1.0.0
```

The script downloads the newest llama.cpp Vulkan build, Gemma 4 E4B (Q4_K_M, from Hugging Face, checksum-verified) with its picture support file from the same repository (`models\gemma-4-E4B-it-mmproj.gguf`; when the repository has none it warns and the included model reads text only) and the Kokoro model into the published app, so it needs a few GB of free disk space and a fast connection. Outputs are written to `artifacts\`: `VoiceChatbotMini-Setup-<version>-win-x64.exe`, its `VoiceChatbotMini-Setup-<version>-win-x64-1.bin`, `-2.bin`, ... parts and `SHA256SUMS.txt`. There is no portable ZIP. The app builds as `VoiceChatbotMini.exe`; its product name and folders are set in `Core/AppPaths.cs`.

Logic without WPF lives in `Core/` and is unit tested in `tests/VoiceChatbot.Tests` (`dotnet test tests/VoiceChatbot.Tests`, runs on any OS). The Live Transcriber's prompts and summary styles (`TranscriptSummaryPrompts`, `TranscriptSummaryStyles`), the notes layout (`TranscriptNotes`), live updates, final notes and Re-summarize all (`TranscriptNotesWriter`), long-transcript splitting (`TranscriptSummarizer`), live-notes timing (`LiveNotesPolicy`), the desktop session state (`TranscriberSessionStore`) and the saved Markdown/text document (`LiveTranscriptText`) are there so the web transcriber shares them. Its page (`PhoneRemoteTranscriberPage`), the values its script takes from those classes, its request limits and file names (`WebTranscriber`) and its background notes jobs (`TranscriberJobs`) are in `Core/` too; the endpoints are in `PhoneRemoteServer.Transcriber.cs`.

## GitHub releases

Mini is built from the `mini` branch. Its GitHub Actions workflow builds the installer (the `.exe` and its `.bin` parts):

- Run **Build Windows release (Mini)** manually from the Actions tab (on the `mini` branch) for test artifacts, uploaded as `VoiceChatbotMini-<version>-win-x64`.
- Push a tag such as `mini-v1.0.0` to create a GitHub Release named **Voice Chatbot Mini 1.0.0**. Mini tags start with `mini-v`, so they never mix with the full app's `v1.0.0` tags and releases.

```powershell
git tag mini-v1.0.0
git push origin mini-v1.0.0
```

## Data and security

- Settings, API keys, downloaded Whisper models, reply audio, memories, saved conversations, transcripts and phone certificates are stored outside the installation directory under `%APPDATA%\VoiceChatbotMini`; AI models downloaded with the model chooser go to `%LOCALAPPDATA%\VoiceChatbotMini\models`; temporary files go to `%TEMP%\VoiceChatbotMini`. The full Voice Chatbot app's `%APPDATA%\VoiceChatbot` is only read once, to copy its `settings.json` on Mini's first run.
- Do not commit `settings.json`, certificates, passwords, API keys or model files.
- The OpenAI API key, Tavily API key and phone remote PIN are encrypted in `settings.json` with Windows DPAPI for your Windows account (they appear as `"dpapi:..."`). Plain-text values from older versions are encrypted the next time the app starts. A settings file copied to another PC or Windows user cannot be decrypted there: those fields are left empty, the app warns once, and you enter them again.
- The phone remote checks its PIN (in constant time) on every API request; spoken replies are fetched by random, unguessable links. After 5 wrong PINs within 10 minutes, that IP address is locked out for 10 minutes: the phone shows "Too many wrong PIN attempts" (HTTP 429), even the right PIN is refused until the lockout ends, and the desktop app posts a note in the chat. A correct PIN resets the count, and **New PIN** lifts all lockouts. Requests without the right PIN are refused before their body is read, and request sizes are capped (64 MB for a voice clip, 256 MB for a message with files or a meeting recording, 8 MB for a web transcriber chunk and 16 MB for its text requests, which accept up to 4,000,000 characters of transcript). The web transcriber page itself contains no data, runs only its own script (Content-Security-Policy) and shows all text as plain text; its files are saved under names the PC generates, never a name or path from the browser. Uploaded files are saved under random names in `%TEMP%\VoiceChatbotMini\phone-*`, so a file name cannot place a file anywhere else.
- The app keeps a daily log in `%APPDATA%\VoiceChatbotMini\logs\app-YYYYMMDD.log` for 7 days: startup and shutdown, every system message shown in the chat, backend errors and crashes. Saved keys and passwords are masked in it. The built-in model's server writes its own `llama-server.log` next to it. Use **App > Open logs folder** in the settings sidebar to attach it to a bug report.
- Each reply writes one timing line to the log, and each spoken reply one more; **Show diagnostics in chat** (App) also shows them in the chat. `Reply timing: first words after 0.9 s, done after 2.4 s (3.1 s after your message); prompt 1350 tokens (1100 reused from cache) at 900 tokens/s; reply 61 tokens at 38 tokens/s; thinking 0 characters` shows where the model's time went (with the built-in model, llama-server's own measurements). `Speech timing: first audio after 0.8 s (4.1 s after your message); synthesis 2.3 s for 18.5 s of audio in 3 pieces, 0.12 x real time; Built-in Kokoro (graphics card)` shows how long it took from the finished reply (and from your message) until the voice started, how fast Kokoro made the audio and where it ran.
- The listen hotkey turns the microphone on from any app, also while the window is hidden in the tray. The tray icon's tooltip shows "Listening...", and **App > Listen hotkey > Off** turns it off. It is registered with Windows only while Voice Chatbot Mini runs.
- Uninstalling the application does not delete `%APPDATA%\VoiceChatbotMini` or the downloaded models in `%LOCALAPPDATA%\VoiceChatbotMini\models`, so reinstalling preserves settings and models. Delete those folders manually to remove all local app data.
