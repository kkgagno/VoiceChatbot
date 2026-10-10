using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

public enum HelpBlockKind
{
    /// <summary>A small heading inside a topic.</summary>
    Heading,
    /// <summary>A paragraph of text.</summary>
    Paragraph,
    /// <summary>A bulleted list (<see cref="HelpBlock.Items"/>).</summary>
    Bullets,
    /// <summary>A numbered list of steps (<see cref="HelpBlock.Items"/>).</summary>
    Steps,
    /// <summary>A highlighted tip.</summary>
    Tip,
    /// <summary>Links to other topics; <see cref="HelpBlock.Items"/> holds their ids.</summary>
    SeeAlso
}

/// <summary>
/// One piece of a help topic. Text and items use a tiny markup that <see cref="HelpMarkup"/> reads:
/// **bold** for button and setting names, `code` for things to type, paths and addresses, and
/// http(s) links written out in full.
/// </summary>
public sealed class HelpBlock
{
    public HelpBlock(HelpBlockKind kind, string text, IReadOnlyList<string>? items = null)
    {
        Kind = kind;
        Text = text ?? "";
        Items = items ?? Array.Empty<string>();
    }

    public HelpBlockKind Kind { get; }
    public string Text { get; }
    public IReadOnlyList<string> Items { get; }
}

/// <summary>A page of the Help window.</summary>
public sealed class HelpTopic
{
    private string? _searchText;

    public HelpTopic(string id, string title, string group, string icon, string summary,
        IReadOnlyList<HelpBlock> blocks, IReadOnlyList<string> keywords, string? sidebarSection)
    {
        Id = id;
        Title = title;
        Group = group;
        Icon = icon;
        Summary = summary;
        Blocks = blocks;
        Keywords = keywords;
        SidebarSection = sidebarSection;
    }

    public string Id { get; }
    public string Title { get; }
    /// <summary>The heading the topic is listed under.</summary>
    public string Group { get; }
    /// <summary>Segoe Fluent Icons / MDL2 glyph shown next to the title.</summary>
    public string Icon { get; }
    /// <summary>One or two sentences under the title.</summary>
    public string Summary { get; }
    public IReadOnlyList<HelpBlock> Blocks { get; }
    /// <summary>Extra words the search finds the topic by (synonyms such as "tts" or "iphone").</summary>
    public IReadOnlyList<string> Keywords { get; }
    /// <summary>The header of the sidebar section (Expander) this topic explains, or null.</summary>
    public string? SidebarSection { get; }

    /// <summary>Everything a search looks at, without markup.</summary>
    public string SearchText => _searchText ??= BuildSearchText();

    private string BuildSearchText()
    {
        var text = new StringBuilder();
        text.Append(Title).Append('\n').Append(HelpMarkup.ToPlainText(Summary)).Append('\n');
        text.Append(string.Join(' ', Keywords)).Append('\n');
        foreach (var block in Blocks)
        {
            if (block.Kind == HelpBlockKind.SeeAlso)
                continue;
            text.Append(HelpMarkup.ToPlainText(block.Text)).Append('\n');
            foreach (var item in block.Items)
                text.Append(HelpMarkup.ToPlainText(item)).Append('\n');
        }

        return text.ToString();
    }
}

public enum HelpSpanKind
{
    Text,
    Bold,
    Code,
    Link
}

public readonly record struct HelpSpan(HelpSpanKind Kind, string Text);

/// <summary>Reads the markup of help text: **bold**, `code` and bare http(s) links.</summary>
public static class HelpMarkup
{
    private static readonly Regex LinkPattern = new(@"https?://[^\s<>""'`]+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits <paramref name="text"/> into plain, bold, code and link spans. A ** or ` without its
    /// closing partner is kept as ordinary text. Links are only found in plain text, so an address
    /// to type (in `code`) is not turned into a link.
    /// </summary>
    public static IReadOnlyList<HelpSpan> Parse(string? text)
    {
        var spans = new List<HelpSpan>();
        if (string.IsNullOrEmpty(text))
            return spans;

        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    FlushPlain(plain, spans);
                    spans.Add(new HelpSpan(HelpSpanKind.Code, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }
            else if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushPlain(plain, spans);
                    spans.Add(new HelpSpan(HelpSpanKind.Bold, text[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }
            }

            plain.Append(text[i]);
            i++;
        }

        FlushPlain(plain, spans);
        return spans;
    }

    /// <summary>The text without markup, as a search or a screen reader sees it.</summary>
    public static string ToPlainText(string? text) => string.Concat(Parse(text).Select(s => s.Text));

    private static void FlushPlain(StringBuilder plain, List<HelpSpan> spans)
    {
        if (plain.Length == 0)
            return;

        var text = plain.ToString();
        plain.Clear();
        var start = 0;
        foreach (Match match in LinkPattern.Matches(text))
        {
            // A sentence that ends with a link: the full stop or comma is not part of it.
            var link = match.Value.TrimEnd('.', ',', ';', ':', ')', '!', '?');
            if (match.Index > start)
                spans.Add(new HelpSpan(HelpSpanKind.Text, text[start..match.Index]));
            spans.Add(new HelpSpan(HelpSpanKind.Link, link));
            start = match.Index + link.Length;
        }

        if (start < text.Length)
            spans.Add(new HelpSpan(HelpSpanKind.Text, text[start..]));
    }
}

/// <summary>
/// The text of the Help window (F1 or Help in the top bar): one topic per sidebar section and per
/// top-bar control, plus getting started, what you need and troubleshooting. Edit the topics here;
/// the window only lays them out. Names in **bold** must match the labels in the app.
/// </summary>
public static class HelpContent
{
    public const string GroupStart = "Start here";
    public const string GroupUsing = "Using the app";
    public const string GroupSidebar = "Settings sidebar";
    public const string GroupProblems = "More help";

    public const string GettingStartedId = "getting-started";
    public const string LiveTranscriberId = "live-transcriber";
    public const string SchedulerId = "scheduler";
    public const string KnowledgeFolderId = "knowledge-folder";

    /// <summary>Words a search ignores, so "how do I get voice" finds the voice topics.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "can", "do", "does", "doesn't", "dont", "don't", "for", "get", "how", "i",
        "in", "is", "isn't", "it", "make", "me", "my", "not", "of", "on", "or", "the", "to", "use", "what",
        "when", "where", "why", "with", "work", "working", "won't", "you"
    };

    public static IReadOnlyList<HelpTopic> Topics { get; } = BuildTopics();

    /// <summary>The groups in the order the topic list shows them.</summary>
    public static IReadOnlyList<string> Groups { get; } = new[] { GroupStart, GroupUsing, GroupSidebar, GroupProblems };

    public static HelpTopic? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Topics.FirstOrDefault(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The topic for a sidebar section, by its header ("Voice Input"), or null.</summary>
    public static HelpTopic? ForSidebarSection(string? header) =>
        string.IsNullOrWhiteSpace(header)
            ? null
            : Topics.FirstOrDefault(t => string.Equals(t.SidebarSection, header.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The topics that match <paramref name="query"/>, best first; all topics in their usual order for
    /// an empty query. Matching ignores case and common words ("how", "do", "my"), and a word matches
    /// the start of a word ("mic" finds "microphone"). Topics that contain every word come first,
    /// ranked by where the words are found (title, then keywords and headings, then the summary and
    /// text). When no topic has every word, topics with the most matching words are returned.
    /// </summary>
    public static IReadOnlyList<HelpTopic> Search(string? query)
    {
        var words = SplitQuery(query);
        if (words.Count == 0)
            return Topics;

        var phrase = string.Join(' ', words);
        var scored = Topics
            .Select((topic, index) => (topic, index, matched: words.Count(w => Contains(topic.SearchText, w))))
            .Where(x => x.matched > 0)
            .ToList();

        var complete = scored.Where(x => x.matched == words.Count).ToList();
        if (complete.Count > 0)
        {
            return complete
                .OrderByDescending(x => Rank(x.topic, words, phrase))
                .ThenBy(x => x.index)
                .Select(x => x.topic)
                .ToList();
        }

        return scored
            .OrderByDescending(x => x.matched)
            .ThenByDescending(x => Rank(x.topic, words, phrase))
            .ThenBy(x => x.index)
            .Select(x => x.topic)
            .ToList();
    }

    private static int Rank(HelpTopic topic, IReadOnlyList<string> words, string phrase)
    {
        var rank = 0;
        if (Contains(topic.Title, phrase))
            rank += 8;
        rank += 3 * words.Count(w => Contains(topic.Title, w));
        var keywords = string.Join(' ', topic.Keywords);
        rank += 2 * words.Count(w => Contains(keywords, w));
        var headings = string.Join('\n', topic.Blocks.Where(b => b.Kind == HelpBlockKind.Heading).Select(b => b.Text));
        rank += 2 * words.Count(w => Contains(headings, w));
        rank += words.Count(w => Contains(topic.Summary, w));
        if (Contains(topic.SearchText, phrase))
            rank += 1;
        return rank;
    }

    private static List<string> SplitQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<string>();

        var words = query
            .Split(new[] { ' ', '\t', '\r', '\n', ',', ';', '?', '!', '"' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ':', '(', ')', '\'', '*', '`'))
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var meaningful = words.Where(w => !StopWords.Contains(w)).ToList();
        return meaningful.Count > 0 ? meaningful : words;
    }

    // The word must start a word in the text: "mic" finds "microphone", but "pin" does not find "typing".
    private static bool Contains(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = at + 1 < text.Length ? text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase) : -1)
        {
            if (at == 0 || !char.IsLetterOrDigit(text[at - 1]))
                return true;
        }

        return false;
    }

    // ==================== The topics ====================

    private static IReadOnlyList<HelpTopic> BuildTopics() => new[]
    {
        // ---------- Start here ----------
        new TopicBuilder(GettingStartedId, "Getting started", GroupStart, "\uE80F",
                "Voice Chatbot Mini is a voice and text assistant for an AI model that runs on your own PC or network. Five steps get you talking to it.")
            .Keywords("start", "setup", "first", "begin", "quick start", "introduction", "overview")
            .Steps(
                "Start a chat model server, for example Ollama with one model downloaded. Mini connects to a server you run; it does not start or stop models for you. See **Set up a chat model**.",
                "Open the settings sidebar (the three-line button at the top left, or Ctrl+B). Under **Chat Backend**, choose the **Provider**, check the address, click **Refresh Models** and pick a **Model**.",
                "Type a message in the box at the bottom and press Enter. The CHAT line at the top of the sidebar shows whether the server is connected.",
                "To talk instead of typing: open **Voice Input**, pick a **Whisper model**, click **Download Model** once and choose your **Microphone**. Then press **Listen** in the top bar.",
                "To hear the answers: set up Kokoro once (see **Voice Output**). **Speak responses** is on by default.")
            .Paragraph("Optional extras: answers from your own files (**Knowledge Folder**), live web search (**Web Search**), the **Live Transcriber** for meetings, prompts that run on a timer (**Scheduler**) and the **Phone Remote** for your phone.")
            .Heading("Finding your way around")
            .Bullets(
                "Left: the settings sidebar, one section per feature. Click a section's title to open or close it. Settings are saved automatically, at the latest when you close the app.",
                "Top: buttons for your conversations, the persona box, and **Listen**, **Transcribe**, **Scheduler**, **Help** and **Stop**.",
                "Middle: the chat. Bottom: the message box with the **Image** and **Document** buttons.",
                "Press F1 any time to open Help. Inside a settings section, F1 opens the topic for that section. Type in the search box above the topic list to find any word, and use **Next** to go through the topics in order.")
            .Tip("Mini keeps your settings, chats and memories on this PC, in `%APPDATA%\\VoiceChatbotMini`.")
            .SeeAlso("what-you-need", "chat-server", "top-bar")
            .Build(),

        new TopicBuilder("what-you-need", "What you need", GroupStart, "\uE9D5",
                "Only a chat model server is required. Everything else is optional and can be added later.")
            .Keywords("requirements", "install", "download", "prerequisites", "checklist", "python", "hardware")
            .Heading("Required")
            .Bullets(
                "**A chat model server**: Ollama (the easiest), llama.cpp, LM Studio or any server with an OpenAI-compatible API, on this PC or on another computer on your network. A model with tool support can search the web and save memories by itself; a vision model can look at pictures you attach.",
                "**Windows 10 (version 1809 or newer) or Windows 11**, 64-bit.")
            .Heading("To talk to it (voice input)")
            .Bullets(
                "A microphone: a headset, webcam microphone or the one built into a laptop.",
                "A Whisper speech recognition model. Pick a size under **Voice Input** and click **Download Model**. It is downloaded once and runs on this PC, on the processor or the graphics card. Your voice is not sent to the internet.")
            .Heading("To hear it (voice output)")
            .Bullets(
                "Kokoro speech on this PC. The app includes the Kokoro scripts; they need Python 3.11, 3.12 or 3.13 (64-bit) and a one-time install. See **Voice Output**.",
                "Or a Kokoro-FastAPI server on another computer (port 8880), entered as **Remote Kokoro host**.")
            .Heading("Optional")
            .Bullets(
                "A Tavily API key for live web search (free plan at https://tavily.com). See **Web Search**.",
                "A folder with your own documents for **Knowledge Folder**.",
                "A phone or tablet on the same Wi-Fi for the **Phone Remote**, and Tailscale (or another VPN) to use it away from home.",
                "AMD Ryzen AI Software and its NPU driver, only if you want speech recognition to run on a Ryzen AI NPU.")
            .Paragraph("Already included: the installer brings yt-dlp, Deno and ffmpeg for YouTube links and phone audio, and Windows has the text recognition (OCR) for scanned PDFs and pictures built in.")
            .SeeAlso("chat-server", "voice-output", "voice-input")
            .Build(),

        new TopicBuilder("chat-server", "Set up a chat model", GroupStart, "\uE7F4",
                "Mini talks to a model server that you start yourself. Pick one of these, then enter its address under Chat Backend.")
            .Keywords("ollama", "llama.cpp", "llama-server", "lm studio", "vllm", "openai", "gguf", "server", "backend", "model", "url", "localhost", "pull")
            .Heading("Option 1: Ollama (easiest)")
            .Steps(
                "Download and install Ollama for Windows from https://ollama.com/download. It runs in the background.",
                "Open PowerShell or Terminal and download a model, for example `ollama pull gemma3:4b`. More models are listed at https://ollama.com/library; small ones (1B to 8B) are the fastest on an ordinary PC.",
                "In Mini, open **Chat Backend**, set **Provider** to `Ollama` and **Ollama URL** to `http://localhost:11434`.",
                "Click **Refresh Models** and choose the model in **Model**.")
            .Tip("Ollama on another PC: on that PC set the environment variable `OLLAMA_HOST` to `0.0.0.0` and restart Ollama, then enter `http://<that PC's IP address>:11434` here.")
            .Heading("Option 2: llama.cpp")
            .Steps(
                "Download llama.cpp from https://github.com/ggml-org/llama.cpp/releases and a model file in GGUF format.",
                "Start the server, for example `llama-server -m C:\\Models\\model.gguf --port 8080 -c 16384 --jinja`. `--jinja` lets the model use tools; add `--host 0.0.0.0` to reach it from other computers.",
                "In Mini set **Provider** to `OpenAI-compatible` and **OpenAI / llama.cpp URL** to `http://localhost:8080/v1` (or `http://<server IP address>:8080/v1`).",
                "Click **Refresh Models**.")
            .Heading("Option 3: LM Studio or another OpenAI-compatible server")
            .Bullets(
                "LM Studio: load a model and start the local server in its Developer tab, then set **Provider** to `OpenAI-compatible` and the URL to `http://localhost:1234/v1`.",
                "vLLM, LocalAI, a hosted service and others: enter the server's address ending in `/v1`. If the server needs a key, enter it in **OpenAI API key (optional)**.")
            .Heading("Good to know")
            .Bullets(
                "Mini never starts, stops or switches model servers. Start the server first, then click **Refresh Models**.",
                "The line under **Context window** shows how much text the server accepts per request and where that number came from.",
                "Pictures you attach need a vision model. Tools (web search, date and time, stock quotes, reading web pages, saving memories) need a model that supports tools, such as an Ollama model tagged tools.")
            .SeeAlso("chat-backend", "troubleshooting")
            .Build(),

        // ---------- Using the app ----------
        new TopicBuilder("top-bar", "The top bar", GroupUsing, "\uE700",
                "The strip above the chat, from left to right.")
            .Keywords("toolbar", "buttons", "menu", "status", "header", "stop", "help", "new chat")
            .Bullets(
                "**Three-line button**: shows or hides the settings sidebar (Ctrl+B).",
                "**Conversations** (the clock button): opens your saved chats (Ctrl+H). See **Chat History**.",
                "**New chat** (the + button): starts a fresh conversation (Ctrl+N). The current one stays in your history.",
                "**Status**: what the assistant is doing (Ready, Listening..., Processing..., Speaking..., Waiting for wake word), with the model in use underneath. The thin line along the bottom of the top bar shows the microphone level.",
                "**Persona box**: switches the system prompt, voice, speech rate and temperature (and the model, if the persona has one) in one step. See **Personas**.",
                "**Listen** / **Stop listening**: turns the microphone on for hands-free talking, and off again (Ctrl+L). See **Listen and the wake word**.",
                "**Transcribe**: opens the Live Transcriber for meetings, calls and videos. While it is open the button reads **Transcribing**.",
                "**Scheduler**: prompts that run by themselves at set times.",
                "**Help**: this window (F1).",
                "**Stop**: stops the answer being written, stops speech and turns listening off (Esc).")
            .SeeAlso("chatting", "listening", LiveTranscriberId, SchedulerId, "personas")
            .Build(),

        new TopicBuilder("chatting", "Typing, pictures and documents", GroupUsing, "\uE724",
                "The message box at the bottom of the window, and what you can send.")
            .Keywords("message", "send", "type", "attach", "paste", "image", "picture", "photo", "pdf", "document", "youtube", "keep doc", "tools", "vision")
            .Bullets(
                "Type and press Enter (or Shift+Enter) to send. Ctrl+Enter starts a new line. Ctrl+K jumps to the message box.",
                "**Image** attaches pictures to the next message (JPG, PNG, BMP, GIF, WebP). Ctrl+V pastes a picture or screenshot. Only a vision model can see them.",
                "**Document** attaches a PDF, Word, Excel, PowerPoint, text or similar file to the next message. Scanned pages are read with Windows OCR (up to 8 pages).",
                "**Keep doc**: keeps the attached document in every following question until you untick it. Without it, the document is only used for the next message.",
                "Paste a YouTube link and ask for a summary: the captions are fetched, or the audio is transcribed when there are none.",
                "Start a message with \"search the web for ...\" to make it search first (needs **Web Search**).",
                "Under a spoken answer, **Replay Audio** plays it again and **Download Audio** saves it. Code blocks have a **Copy** button.")
            .Heading("What the assistant does by itself")
            .Paragraph("With **Let the model use tools** on (Chat Backend), the model decides when to search the web, check the date and time, get a stock quote, read a web page you paste, or save a memory. A short note in the chat shows each tool it used. Say \"remember that my dog is called Rex\" and it can keep that as a memory.")
            .SeeAlso("chat-backend", "conversation", "web-search")
            .Build(),

        new TopicBuilder("listening", "Listen and the wake word", GroupUsing, "\uE720",
                "Talk to the assistant hands-free, with or without a wake word.")
            .Keywords("listen", "stop listening", "hands-free", "talk", "speak", "voice", "wake word", "hey onyx", "hotkey", "microphone")
            .Bullets(
                "Press **Listen** in the top bar (or Ctrl+L, or the listen hotkey Ctrl+Alt+Space from any app). The button changes to **Stop listening** and the microphone stays on.",
                "Speak normally. When you pause for the **Silence timeout** (2.4 seconds by default), Whisper turns what you said into text and sends it. After the answer has been spoken, it listens again.",
                "Press **Stop listening** (or Ctrl+L), **Stop** or Esc to turn the microphone off.",
                "The thin line along the bottom of the top bar moves with your voice, so you can see that the microphone hears you.")
            .Heading("Wake word")
            .Bullets(
                "Turn on **Only respond after the wake word** under **Voice Input**. The microphone stays on, but the assistant only answers when you say the wake word (`hey onyx` unless you change it). The status reads Waiting for wake word.",
                "Say it together with your question (\"Hey Onyx, what's the weather?\"), or say it alone, wait for the short Windows sound, and ask within about 8 seconds.",
                "Everything else is ignored, so it does not answer the TV or other people in the room.",
                "Change the phrase in the **Wake word** box. It is matched loosely, so \"Hey, Onix.\" also counts.")
            .Tip("Listen needs a Whisper model. If nothing happens, open **Voice Input**, choose a model, click **Download Model** and check the **Microphone**.")
            .SeeAlso("voice-input", "app")
            .Build(),

        new TopicBuilder(LiveTranscriberId, "Live Transcriber and Live notes", GroupUsing, "\uE8FD",
                "Transcribe opens a separate window that writes down a meeting, call or video as it happens, and lets the chat model take notes.")
            .Keywords("transcribe", "transcript", "meeting", "minutes", "call", "notes", "summary", "summarize", "dictation", "pc audio", "record")
            .Steps(
                "Click **Transcribe** in the top bar.",
                "Choose the source: **Microphone** (the one chosen under **Voice Input**) or **PC audio** (everything the PC plays, such as a video call or a YouTube video).",
                "Click **Start** (Ctrl+R). Lines appear as `[03:12] ...`, timed from the start of the session.",
                "Click **Stop** when you are done. The session is saved automatically in `%APPDATA%\\VoiceChatbotMini\\transcripts`.")
            .Heading("Notes and summaries")
            .Bullets(
                "**Live notes**: while recording, every 5, 10 or 15 minutes (choose next to it) the chat model writes notes on what was said since the last update as a new section under NOTES BY TIME, and refreshes SUMMARY SO FAR at the top.",
                "The style box sets how the final summary is written: Summary, Action items, Meeting notes or Key points.",
                "The button next to it reads **Update notes now** while recording, **Summarize** when stopped with empty notes, and **Re-summarize all** to rebuild the notes from the whole transcript.",
                "**Stop** adds a last section and writes a full summary in the chosen style.",
                "**System message** (folded away at the top) is the instruction the model gets for notes and summaries. **Reset to default** restores it.")
            .Heading("Other buttons")
            .Bullets(
                "**Copy** the transcript or the notes, **Save...** them as Markdown or text (Ctrl+S), or **Open folder** to see saved sessions.",
                "**Send to chat** gives the transcript and notes to the main chat, so you can ask questions about the meeting there.",
                "**Clear** saves the session and starts a new one. Closing and reopening the window brings your last session back.",
                "When stopped you can edit the transcript, for example to fix names before summarizing. The small and large A buttons, or Ctrl+mouse wheel, change the text size.")
            .Tip("Notes are written by your chat model, so the model server must be running. While the assistant is speaking, the transcriber pauses so its voice is not written down. The same transcriber works in a browser on the **Phone Remote**, at `/transcribe`.")
            .SeeAlso("phone-remote", "voice-input")
            .Build(),

        new TopicBuilder(SchedulerId, "Scheduler", GroupUsing, "\uE787",
                "Run a prompt by itself at a set time, once or on repeat, for example a news summary every morning.")
            .Keywords("schedule", "timer", "recurring", "daily", "weekly", "hourly", "monthly", "automatic", "task", "reminder")
            .Steps(
                "Click **Scheduler** in the top bar, then **New**.",
                "Enter a **Name** and the **Prompt** to send, for example \"Summarize today's top tech news in five bullet points\".",
                "Choose the **Next Date**, the **Time** (such as `09:00 AM`) and the **Recurrence**: Once, Hourly, Daily, Weekly or Monthly.",
                "Leave **Enabled** ticked. Tick **Show result in main chat** to have the answer appear in the chat too.",
                "Click **Save Task**. **Run Now** runs it straight away to try it.")
            .Heading("Results")
            .Bullets(
                "Every run is listed under **Saved Runs**. **Runs To Keep** sets how many are kept (5 unless you change it).",
                "Select a run to read its **Response**. **Send to Main Chat** puts it in the chat, and **Play Audio** plays the spoken version (made when **Speak responses** is on).",
                "**Delete** removes the selected task.")
            .Heading("Good to know")
            .Bullets(
                "Tasks run only while Mini is open; minimized or in the tray is fine. A run missed while the app was closed runs once when it starts again if it was missed less than 12 hours ago; older ones are skipped.",
                "Prompts use the model, system prompt and temperature selected in the app. A prompt that asks for current information (news, prices, \"search the web for ...\") gets a web search first when **Web Search** is on and has a key. The other tools are not used.",
                "A run that fails is tried again later. Tasks are saved in `%APPDATA%\\VoiceChatbotMini\\scheduler.json`.")
            .Build(),

        new TopicBuilder("personas", "Personas", GroupUsing, "\uE77B",
                "A persona is a saved set of system prompt, voice, speech rate and temperature (and optionally a model), so you can change the assistant's character in one step.")
            .Keywords("persona", "character", "role", "profile", "preset", "system prompt", "voice")
            .Bullets(
                "Pick a persona in the box in the top bar. The prompt, voice, speech rate and temperature switch to it, and the model too if the persona has one.",
                "To make one: set the **System prompt** (Chat Backend), the voice and speech rate (Voice Output) and the temperature, type a name under PERSONAS in **Chat Backend** and click **Save as new persona** (or press Enter).",
                "**Update persona** saves your current settings into the active persona. Changes you make are kept until you pick another persona, but they are only saved into it when you click **Update persona**.",
                "Turn on **Include the current model** before saving or updating if the persona should also switch the model.",
                "**Delete persona** asks for a second click, then deletes the active persona. The last one cannot be deleted.",
                "A **Default** persona is made from your settings the first time the app starts. Saved chats remember their persona.")
            .SeeAlso("chat-backend", "voice-output")
            .Build(),

        // ---------- Settings sidebar ----------
        new TopicBuilder("chat-backend", "Chat Backend", GroupSidebar, "\uE8F2",
                "Which model server to use, and how the model should answer.")
            .Sidebar("Chat Backend")
            .Keywords("provider", "ollama", "openai", "llama.cpp", "url", "api key", "model", "refresh models", "system prompt", "temperature", "tokens", "context", "num_ctx", "markdown", "thinking", "tools")
            .Bullets(
                "**Provider**: `Ollama`, or `OpenAI-compatible` for llama.cpp, LM Studio, vLLM and similar servers.",
                "**Ollama URL**: where Ollama runs, normally `http://localhost:11434`.",
                "**OpenAI / llama.cpp URL**: the server's address ending in `/v1`, for example `http://localhost:8080/v1`.",
                "**OpenAI API key (optional)**: only for servers that need a key. It is saved encrypted.",
                "**Model** and **Refresh Models**: Refresh Models loads the list from the server; pick a model or type its name.",
                "**System prompt**: standing instructions for the assistant, such as its personality, how long answers should be or which language to use.",
                "**Temperature**: lower (0.2) gives steadier, more predictable answers; higher (1.0 and up) more varied ones. 0.7 is a good start.",
                "**Max reply tokens**: the longest normal answer (2048 unless you change it). Requests for code or SVG get more room.",
                "**Context window**: how much text one request may hold. The line under it shows what the server reports and where that came from. The box is used when the server reports nothing, and is sent to Ollama as num_ctx (0 means the model's full window). A larger window uses more graphics memory.",
                "**Let the model use tools**: the model can search the web, check the date and time, get stock quotes, read web pages and save memories by itself. Models without tool support simply chat.",
                "**Format replies (Markdown)**: shows headings, lists, tables, links and code blocks in finished answers. Formatting is never read aloud.",
                "**Hide model thinking**: asks the server to skip the model's thinking phase, so answers start sooner. Thinking text is never shown, saved or spoken.")
            .Paragraph("The PERSONAS part of this section is explained under **Personas**.")
            .SeeAlso("chat-server", "personas")
            .Build(),

        new TopicBuilder("voice-output", "Voice Output", GroupSidebar, "\uE767",
                "Spoken answers with Kokoro, on this PC or on another computer.")
            .Sidebar("Voice Output")
            .Keywords("kokoro", "tts", "text to speech", "speech", "speak", "voice", "sound", "speaker", "audio", "python", "8880", "8766", "kokoro-fastapi")
            .Bullets(
                "**Speak responses**: reads answers aloud. Turn it off for text only. Code blocks are never read.",
                "**Start speaking before the reply finishes**: speaks in parts while the model is still writing (needs **Stream responses** under Conversation). Off sounds the most natural.",
                "**Remote Kokoro host** and **Test**: the address of a Kokoro-FastAPI server, such as `192.168.1.50` (port 8880 is assumed), `192.168.1.50:8880` or `http://tts-box:8880/v1`. **Test** checks it and loads its voices. Leave it blank to use only Kokoro on this PC.",
                "**Engine**: **Auto** tries the remote host first and falls back to Kokoro on this PC; **Remote only** and **Local only** use just one of them.",
                "**Voice** and the play button next to it: choose a voice and hear a sample.",
                "**Speech rate** (-5 to +5) and **Volume**.")
            .Paragraph("The SPEECH line at the top of the sidebar shows which Kokoro is in use and whether it answers.")
            .Heading("Set up Kokoro on this PC (once)")
            .Steps(
                "Install Python 3.11, 3.12 or 3.13 (64-bit) from https://www.python.org/downloads/windows/ and tick Add Python to PATH in its installer.",
                "Open PowerShell in the app's `Tools\\Kokoro` folder. For the installed app that is `%LOCALAPPDATA%\\Programs\\VoiceChatbotMini\\Tools\\Kokoro`.",
                "Run `powershell -ExecutionPolicy Bypass -File .\\install-kokoro.ps1` and wait until it says the dependencies are installed.",
                "Restart Mini. It starts its own Kokoro server on `http://127.0.0.1:8766` when it needs it. The first answer takes longer while Kokoro loads (and, the very first time, downloads its voice model).")
            .Tip("To make Mini use a particular Python, set the environment variable `VOICECHATBOT_PYTHON` to the full path of its python.exe.")
            .Heading("Use Kokoro on another computer")
            .Paragraph("Run Kokoro-FastAPI (https://github.com/remsky/Kokoro-FastAPI) on a computer on your network, for example one with a graphics card. It listens on port 8880. Enter that computer's address in **Remote Kokoro host** and click **Test**. Its firewall must allow port 8880.")
            .SeeAlso("troubleshooting", "personas")
            .Build(),

        new TopicBuilder("voice-input", "Voice Input", GroupSidebar, "\uE720",
                "The microphone, and the speech recognition (Whisper) that turns what you say into text.")
            .Sidebar("Voice Input")
            .Keywords("microphone", "mic", "whisper", "speech recognition", "stt", "language", "download model", "gpu", "cpu", "vulkan", "ryzen", "npu", "silence", "noise", "wake word")
            .Bullets(
                "**Microphone** and the refresh button: choose the input device. Click refresh after plugging in a headset. If the chosen microphone cannot be opened, the Windows default microphone is used.",
                "**Recognition language**: the language you speak.",
                "**Whisper model** and **Download Model**: `tiny` is the fastest, `base` is light, `small` (the default) is the best balance and `medium` is the most accurate but slow. Download the size you choose once; it is saved in `%APPDATA%\\VoiceChatbotMini`.",
                "**Use GPU for speech recognition**: runs Whisper on the graphics card (AMD, NVIDIA or Intel, through Vulkan). Often faster; turn it off if words repeat or go missing. Off uses the processor (CPU), which works on every PC. The line under it shows what Whisper runs on. Turning it on after starting with it off needs a restart of the app.",
                "**Transcription backend**: `Whisper.net` is built in and recommended. `AMD Ryzen AI Whisper` uses a Ryzen AI NPU and needs AMD's Ryzen AI Software installed separately; the \"with fallback\" choice uses Whisper.net when that fails.",
                "**Ryzen AI command**: the command that transcribes a recording on the NPU (`{input}` is replaced by the file). Leave it as it is unless you use Ryzen AI.",
                "**Silence timeout (s)**: how long a pause ends what you are saying. Raise it if you get cut off in the middle of a sentence.",
                "**Noise suppression**: how loud a sound must be to count as speech. Raise it if background noise starts recordings; lower it if a quiet voice is missed.",
                "**Only respond after the wake word** and **Wake word**: see **Listen and the wake word**.")
            .SeeAlso("listening", "troubleshooting")
            .Build(),

        new TopicBuilder("phone-remote", "Phone Remote", GroupSidebar, "\uE8EA",
                "Use the assistant from a phone, a tablet or another computer's browser. The phone sends your voice and text to this PC; Whisper, the model and Kokoro all run here.")
            .Sidebar("Phone Remote")
            .Keywords("phone", "iphone", "android", "mobile", "tablet", "remote", "browser", "wifi", "wi-fi", "pin", "certificate", "cer", "https", "port", "5100", "tailscale", "vpn", "transcribe", "firewall")
            .Heading("Set it up")
            .Steps(
                "Make sure the phone and this PC are on the same Wi-Fi network.",
                "Open **Phone Remote** and click **Start Phone Remote**. If Windows asks whether to allow the app on networks, allow it on private networks.",
                "The status box shows the address (such as `https://192.168.1.20:5100/`) and the PIN. **Copy URL** copies the address.",
                "Open that address in the phone's browser. The browser warns about the certificate because this PC made it: on an iPhone, install and trust the certificate once (see below); elsewhere choose Advanced and continue to the page.",
                "Enter the PIN. The page remembers it.")
            .Heading("Options")
            .Bullets(
                "**Start with app**: starts the remote every time Mini starts (turning it on also starts it now).",
                "**Play audio on phone**: the phone plays the spoken answer (needs **Speak responses**).",
                "**Port**: `5100` unless you change it. Choose another one, such as `5101`, if something else uses it, for example the full Voice Chatbot app's remote.",
                "**PIN** and **New PIN**: the remote always needs a PIN. Leave the box empty for a random 6-digit PIN, or type your own (4 to 64 characters, no spaces). **New PIN** makes a new one and lifts lockouts. After 5 wrong PINs within 10 minutes, that device is locked out for 10 minutes.",
                "**Certificate**: opens the folder with the `.cer` certificate file for the phone.")
            .Heading("On the phone")
            .Bullets(
                "**Hold to Talk**: hold the button, speak, let go. **Live Mode** listens hands-free; **Long Talk** allows longer pauses.",
                "Type in the box and tap **Send**. **Files** attaches pictures and documents, **Stop Audio** stops playback and **Test Mic** checks the microphone.",
                "**Record Meeting** records a longer stretch; tap Send afterwards to transcribe it.",
                "**Transcribe** at the top of the page opens the Live Transcriber in the browser (also at `https://<PC address>:<port>/transcribe`), from any device on the network. On a PC with Chrome or Edge it can also record a browser tab's sound.",
                "Messages from the phone also appear in the chat on the PC.")
            .Heading("iPhone certificate")
            .Steps(
                "Click **Certificate**, send the `.cer` file to the iPhone (AirDrop or email) and open it there.",
                "Install it in Settings, General, VPN & Device Management.",
                "Turn on full trust for it in Settings, General, About, Certificate Trust Settings.")
            .Paragraph("If the PC's address changes, start the remote again, and install the new certificate if Safari warns.")
            .Heading("Away from home")
            .Paragraph("Do not open the port on your router. Instead install Tailscale (https://tailscale.com) on the PC and the phone, sign in with the same account, and open `https://<PC's Tailscale address>:5100/`. The browser warns about the certificate for that address; continue to the page.")
            .SeeAlso(LiveTranscriberId, "troubleshooting")
            .Build(),

        new TopicBuilder("conversation", "Conversation", GroupSidebar, "\uE81C",
                "How much of the chat the model sees, whether answers stream in, and long-term memories.")
            .Sidebar("Conversation")
            .Keywords("memory", "memories", "remember", "context", "stream", "streaming", "copy chat", "save chat", "clear chat", "export")
            .Bullets(
                "**Max context messages**: how many recent messages are sent along with each new one (20 unless you change it). More gives the model more to go on but makes every request longer.",
                "**Stream responses**: shows the answer as it is being written.",
                "**Memories in prompt**: **Relevant** sends only the saved memories that match your message (up to **Matching memories**, 4 unless you change it, plus the newest one); **All** sends every memory every time.",
                "**Save Memory**: sums up the current chat as a memory the assistant keeps in later chats.",
                "**View Memory**: lists your memories. **Edit** or **Delete** one, **Add** your own, or **Clear All**.",
                "**Copy Chat** copies the conversation; **Save Chat** saves it as a Markdown file.",
                "**Clear Chat** empties the chat and starts a new conversation. The old one stays in **Chat History**.")
            .Tip("You can also say \"remember that ...\" and the model can save it as a memory itself (with tools on). Memories are kept in `%APPDATA%\\VoiceChatbotMini\\memory`.")
            .SeeAlso("chat-history")
            .Build(),

        new TopicBuilder("chat-history", "Chat History", GroupSidebar, "\uE823",
                "Every chat is saved on this PC so you can open it again later.")
            .Sidebar("Chat History")
            .Keywords("history", "conversations", "saved chats", "old chats", "search", "rename", "delete")
            .Bullets(
                "**Save conversations**: on unless you turn it off. Off stops saving new messages.",
                "**Browse** (or Ctrl+H, or the clock button in the top bar) opens Conversations, with chats grouped by Today, Yesterday, Previous 7 days and Older.",
                "Search titles and message text in the box at the top; Enter opens the first match.",
                "Click a chat to open it again, with its messages, audio and persona. Point at a chat to rename or delete it.",
                "**Open Folder** shows the saved files in `%APPDATA%\\VoiceChatbotMini\\conversations`.",
                "**New chat** (the + button, Ctrl+N) starts a fresh conversation; the previous one stays here.")
            .SeeAlso("conversation")
            .Build(),

        new TopicBuilder(KnowledgeFolderId, "Knowledge Folder", GroupSidebar, "\uE8B7",
                "Let the assistant answer from your own documents, such as manuals, contracts, notes or tax forms. The files stay on this PC and are never changed.")
            .Sidebar("Knowledge Folder")
            .Keywords("documents", "files", "folder", "pdf", "word", "excel", "ocr", "scan", "index", "reindex", "rag", "view text", "onedrive")
            .Steps(
                "Click **Browse** and choose the folder (or type its path and press Enter). Subfolders are included.",
                "Turn on **Use my documents**. The files are read in the background; the status line shows the progress.",
                "Ask about your documents in the chat. A short note shows which files were used.")
            .Heading("Options and buttons")
            .Bullets(
                "**Excerpts per message (at least)**: the fewest passages (about 900 characters each) added to a message about your documents (4 unless you change it). More are added when they match well and there is room; a small folder is sent whole.",
                "**Reindex**: reads new and changed files now and tries again on files that could not be read, for example after adding an OCR language. While reading, the button reads **Stop**.",
                "**Files**: every file in the folder with its status: indexed, could not be read, or skipped and why. Select an indexed file and click **View text** to see exactly what the assistant reads from it, with **Copy**. Double-click a file to show it in Explorer.")
            .Heading("What it can read")
            .Bullets(
                "PDF (scanned pages too, up to 200 pages with OCR), Word (.docx, .doc, .rtf, .odt), Excel (.xlsx, .xlsm, .ods), PowerPoint (.pptx, .odp), emails (.eml, .msg), web pages (.html, .mht), text, Markdown, CSV, JSON, XML and log files.",
                "Pictures (.png, .jpg, .gif, .bmp, .tif, .webp, .heic), with the text recognition (OCR) built into Windows. If Windows has no OCR language, add one in Settings, Time & language, Language & region.",
                "Skipped: files over 25 MB, hidden files, Office lock files, videos and archives. Password-protected files cannot be read.")
            .Tip("OneDrive files that are online only are downloaded when they are read. If that fails, right-click the folder in File Explorer and choose Always keep on this device.")
            .SeeAlso("chatting")
            .Build(),

        new TopicBuilder("web-search", "Web Search", GroupSidebar, "\uE774",
                "Up-to-date answers from the web through Tavily, a search service made for AI assistants.")
            .Sidebar("Web Search")
            .Keywords("tavily", "internet", "online", "search", "news", "api key", "google", "current")
            .Steps(
                "Go to https://tavily.com and create an account. The free plan includes a number of searches every month.",
                "Copy your API key from the Tavily dashboard (it starts with `tvly-`).",
                "Paste it into **Tavily API key** and keep **Enable web search** on.")
            .Heading("How it is used")
            .Bullets(
                "With **Let the model use tools** on (Chat Backend), the model searches by itself when a question needs current information.",
                "Start a message with \"search the web for ...\" or say \"look it up online\" to make it search.",
                "Without a key the chat says \"Web search is on, but no Tavily API key is set.\"",
                "The key is saved encrypted on this PC and only sent to Tavily.")
            .SeeAlso("chat-backend")
            .Build(),

        new TopicBuilder("app", "App", GroupSidebar, "\uE713",
                "The look of the app, the tray icon, the listen hotkey and the log files.")
            .Sidebar("App")
            .Keywords("theme", "dark", "light", "tray", "minimize", "hotkey", "shortcut", "logs", "log file", "diagnostics", "secrets", "encrypted")
            .Bullets(
                "**Theme**: Dark, Light, or Use Windows setting (follows the Windows light or dark mode).",
                "The box with the lock shows that API keys and the phone PIN are saved encrypted for your Windows account.",
                "**Open logs folder**: shows today's log file (`%APPDATA%\\VoiceChatbotMini\\logs\\app-YYYYMMDD.log`). There is one file per day, kept for 7 days, with keys and passwords masked. Attach it when you report a problem.",
                "**Show diagnostics in chat**: shows token counts and the speech recognition backend after each message. They are always written to the log.",
                "**Minimize to tray**: minimizing hides the window in the tray (the icons next to the clock) instead of the taskbar. Double-click the tray icon to bring it back; right-click it for Open, **Listen** / **Stop listening**, **Speak responses** and Exit.",
                "**Listen hotkey**: Ctrl+Alt+Space (the default), Ctrl+Shift+Space, Ctrl+Alt+L or Off. It works in any app, also while Mini is minimized, and turns listening on and off like the **Listen** button. If another app already uses the keys, the box under it says so.")
            .SeeAlso("shortcuts", "privacy", "troubleshooting")
            .Build(),

        // ---------- More help ----------
        new TopicBuilder("shortcuts", "Keyboard shortcuts", GroupProblems, "\uE765",
                "Keys that work in the main window, plus the listen hotkey that works everywhere.")
            .Keywords("keys", "keyboard", "hotkey", "f1", "ctrl", "esc", "enter")
            .Bullets(
                "**F1**: Help. Inside a settings section it opens the help for that section.",
                "**Enter** or **Shift+Enter**: send. **Ctrl+Enter**: new line. **Ctrl+V**: paste text or a picture.",
                "**Ctrl+L**: Listen / Stop listening.",
                "**Esc**: stop the answer, speech and listening (or close Conversations when it is open).",
                "**Ctrl+B**: show or hide the settings sidebar. **Ctrl+K**: go to the message box.",
                "**Ctrl+H**: Conversations. **Ctrl+N**: new chat.",
                "**Ctrl+Alt+Space** (from any app): Listen / Stop listening. Change it under **App**.",
                "Live Transcriber: **Ctrl+R** start or stop, **Ctrl+S** save, **Ctrl+mouse wheel** or **Ctrl+plus** / **Ctrl+minus** text size, **Ctrl+0** normal size.",
                "In Help: **Ctrl+F** search, **Esc** close.")
            .Build(),

        new TopicBuilder("privacy", "Your data and privacy", GroupProblems, "\uE72E",
                "What Mini stores, where, and what leaves your PC.")
            .Keywords("data", "privacy", "files", "folder", "appdata", "backup", "uninstall", "encryption", "full app", "settings.json")
            .Bullets(
                "Everything Mini saves stays on this PC in `%APPDATA%\\VoiceChatbotMini`: settings.json, conversations, memories, transcripts, the knowledge index, scheduled tasks, logs, the phone certificate, Whisper models and spoken answers. Temporary files go to `%TEMP%\\VoiceChatbotMini`.",
                "What you say and type only goes to the servers you set up: your model server, your Kokoro server, Tavily for web searches, and YouTube when you paste a YouTube link.",
                "API keys and the phone PIN are encrypted for your Windows account. On another PC or Windows user they cannot be read; enter them again there.",
                "Uninstalling keeps `%APPDATA%\\VoiceChatbotMini`, so a reinstall keeps your settings. Delete that folder to remove all of Mini's data.",
                "Mini can be installed next to the full Voice Chatbot app; each keeps its own folders. On its first start, Mini copies only the full app's settings.json.")
            .Build(),

        new TopicBuilder("troubleshooting", "Troubleshooting", GroupProblems, "\uE90F",
                "Common problems and what to check first. The log file has the details: every message shown in the chat and every error is written there.")
            .Keywords("problem", "error", "not working", "broken", "fix", "help", "log", "logs", "silent", "no sound", "no voice", "disconnected", "firewall", "reachable")
            .Heading("Where the logs are")
            .Paragraph("Click **Open logs folder** under **App**, or paste `%APPDATA%\\VoiceChatbotMini\\logs` into the File Explorer address bar. There is one file per day (`app-YYYYMMDD.log`), kept for 7 days.")
            .Heading("No model answers")
            .Bullets(
                "Look at the CHAT line at the top of the sidebar. Disconnected means the server cannot be reached: check that Ollama or your server is running and that the URL under **Chat Backend** is right (`http://localhost:11434` for Ollama, an address ending in `/v1` for the others).",
                "Click **Refresh Models**. \"Failed to load models\" in the chat gives the reason; \"Please select a model first!\" means the **Model** box is empty.",
                "A server on another PC must accept connections from the network (Ollama: `OLLAMA_HOST=0.0.0.0`; llama.cpp: `--host 0.0.0.0`), and that PC's firewall must allow the port.",
                "Very slow answers: try a smaller model. \"The model returned an empty answer\": try another model, or turn on **Hide model thinking**.")
            .Heading("No voice: answers are not spoken")
            .Bullets(
                "Check **Speak responses** and **Volume** under **Voice Output**, and the Windows volume and speakers.",
                "Look at the SPEECH line in the sidebar. With **Remote Kokoro host** empty, Kokoro on this PC needs Python and the one-time install (see **Voice Output**). If it never worked, run `install-kokoro.ps1` again and read its messages.",
                "Click the play button next to **Voice** to test a voice.",
                "**Engine** set to **Remote only** without a host means no speech.")
            .Heading("Kokoro is not reachable")
            .Bullets(
                "Click **Test** next to **Remote Kokoro host**. Check that the Kokoro-FastAPI server is running and that the address and port (8880) are right; on that computer, `http://localhost:8880/docs` should open.",
                "The firewall on the Kokoro computer must allow port 8880.",
                "With **Engine** on **Auto**, Mini uses Kokoro on this PC while the remote one is down, and keeps checking in the background.",
                "Kokoro on this PC uses port 8766. \"The local Kokoro server did not start (is port 8766 in use?)\" in the log means another program uses that port.")
            .Heading("The microphone hears nothing")
            .Bullets(
                "Choose the right **Microphone** under **Voice Input**, and click its refresh button after plugging in a device. The thin line under the top bar should move while you speak.",
                "Windows may block it: in Settings, Privacy & security, Microphone, turn on microphone access and Let desktop apps access your microphone.",
                "No Whisper model yet: choose one and click **Download Model**. The chat says when the model is missing.",
                "Lower **Noise suppression** if a quiet voice is missed; raise **Silence timeout (s)** if you get cut off.",
                "With **Only respond after the wake word** on, anything without the wake word is ignored; the status then reads Waiting for wake word.",
                "Words repeated or missing: turn off **Use GPU for speech recognition**.")
            .Heading("The phone page is silent or does not open")
            .Bullets(
                "Use the `https://` address from **Copy URL**, on the same Wi-Fi. If the page does not open, allow the app through Windows Firewall for private networks and check the **Port**.",
                "No sound: turn on **Play audio on phone** and **Speak responses**, and check the phone's volume and silent switch. If Safari blocks playback, tap the audio play button once.",
                "The phone's microphone hears nothing: allow the microphone for the page in the browser's settings, then tap **Test Mic**. Phones stop the microphone when the screen locks or you switch apps.",
                "PIN refused: use the PIN shown under **Phone Remote**. After 5 wrong tries the device is locked out for 10 minutes; **New PIN** lifts that.",
                "Port in use: another program (for example the full Voice Chatbot app's remote) has it. Enter another **Port**, such as `5101`.")
            .Heading("Other problems")
            .Bullets(
                "The listen hotkey does nothing: another app may own those keys. Choose other keys under **App**, **Listen hotkey**.",
                "Keys or the PIN are empty after moving to another PC or Windows user: they are encrypted for the old account. Enter them again.")
            .SeeAlso("what-you-need", "chat-server", "voice-output", "voice-input", "phone-remote")
            .Build()
    };

    private sealed class TopicBuilder
    {
        private readonly string _id;
        private readonly string _title;
        private readonly string _group;
        private readonly string _icon;
        private readonly string _summary;
        private readonly List<HelpBlock> _blocks = new();
        private readonly List<string> _keywords = new();
        private string? _sidebarSection;

        public TopicBuilder(string id, string title, string group, string icon, string summary)
        {
            _id = id;
            _title = title;
            _group = group;
            _icon = icon;
            _summary = summary;
        }

        public TopicBuilder Sidebar(string expanderHeader)
        {
            _sidebarSection = expanderHeader;
            return this;
        }

        public TopicBuilder Keywords(params string[] words)
        {
            _keywords.AddRange(words);
            return this;
        }

        public TopicBuilder Heading(string text) => Add(new HelpBlock(HelpBlockKind.Heading, text));

        public TopicBuilder Paragraph(string text) => Add(new HelpBlock(HelpBlockKind.Paragraph, text));

        public TopicBuilder Bullets(params string[] items) => Add(new HelpBlock(HelpBlockKind.Bullets, "", items));

        public TopicBuilder Steps(params string[] items) => Add(new HelpBlock(HelpBlockKind.Steps, "", items));

        public TopicBuilder Tip(string text) => Add(new HelpBlock(HelpBlockKind.Tip, text));

        public TopicBuilder SeeAlso(params string[] topicIds) => Add(new HelpBlock(HelpBlockKind.SeeAlso, "", topicIds));

        public HelpTopic Build() =>
            new(_id, _title, _group, _icon, _summary, _blocks.ToArray(), _keywords.ToArray(), _sidebarSection);

        private TopicBuilder Add(HelpBlock block)
        {
            _blocks.Add(block);
            return this;
        }
    }
}
