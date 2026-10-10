using System;
using System.Security.Cryptography;

namespace VoiceChatbot;

/// <summary>
/// The web transcriber page (GET /transcribe on the phone remote): records the microphone or, in desktop
/// Chrome/Edge, a tab's or the system's audio, cuts it at pauses like the desktop Live Transcriber, sends the
/// chunks one at a time to the PC to transcribe and offers the same notes by time, live notes, Re-summarize all
/// and output buttons (the notes are written on the PC by Core/TranscriptNotesWriter, as background jobs the page
/// polls).
/// The page holds no data; every API call carries the PIN, remembered under the same key as the main page.
/// All text is put on the page with textContent or form values, never parsed as HTML.
/// </summary>
public static class PhoneRemoteTranscriberPage
{
    private const string NoncePlaceholder = "__NONCE__";
    private const string ConfigPlaceholder = "__TRANSCRIBER_CONFIG__";

    private static readonly Lazy<string> PageWithConfig = new(() =>
        Template.Replace(ConfigPlaceholder, WebTranscriber.ConfigJson(), StringComparison.Ordinal));

    /// <summary>A fresh random nonce for one response's Content-Security-Policy (hex, 128 bits).</summary>
    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>The page with its inline script and style marked with <paramref name="nonce"/>.</summary>
    public static string Build(string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            throw new ArgumentException("A nonce is required.", nameof(nonce));
        return PageWithConfig.Value.Replace(NoncePlaceholder, nonce, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the page's own inline script and style run, and it can only talk to the remote itself, so even
    /// text that slipped into the page as markup could not run code or send the transcript elsewhere.
    /// </summary>
    public static string ContentSecurityPolicy(string nonce) =>
        $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'self'; " +
        "img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
  <meta name="referrer" content="no-referrer">
  <title>Voice Chatbot Mini Transcribe</title>
  <style nonce="__NONCE__">
    :root { color-scheme: dark; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; --fs: 18px; }
    * { box-sizing: border-box; }
    [hidden] { display: none !important; }
    html { width: 100%; overflow-x: hidden; -webkit-text-size-adjust: 100%; text-size-adjust: 100%; }
    body { width: 100%; overflow-x: hidden; margin: 0; background: #101114; color: #f5f5f5; }
    main { width: 100%; max-width: 1400px; margin: 0 auto; min-height: 100vh; min-height: 100dvh; display: flex; flex-direction: column; gap: 8px; padding: 10px 16px calc(10px + env(safe-area-inset-bottom)); }
    header { display: flex; align-items: center; gap: 8px; }
    h1 { font-size: 16px; margin: 0; white-space: nowrap; }
    .navlink { color: white; background: #2d3436; border-radius: 7px; padding: 8px 10px; font-weight: 700; font-size: 13px; text-decoration: none; white-space: nowrap; }
    #modelState { flex: 1; min-width: 0; color: #aeb3bd; font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; text-align: right; }
    .toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
    button { border: 0; border-radius: 7px; color: white; background: #00a884; padding: 9px 12px; font-weight: 700; font-size: 13px; line-height: 1.1; font-family: inherit; cursor: pointer; touch-action: manipulation; }
    button:disabled { opacity: .5; cursor: default; }
    button.secondary { background: #2d3436; }
    select, input { min-width: 0; max-width: 100%; border: 1px solid #373b45; background: #181a20; color: white; border-radius: 7px; padding: 8px 9px; font-size: 16px; font-family: inherit; }
    :focus-visible { outline: 2px solid #246bfe; outline-offset: 2px; }
    #startStop { min-width: 120px; padding: 11px 18px; font-size: 15px; }
    #startStop.recording { background: #d63031; }
    #startStop.finishing { background: #636e72; }
    .clock { display: inline-flex; align-items: center; gap: 7px; padding: 0 4px; font-size: 20px; font-weight: 700; font-variant-numeric: tabular-nums; }
    #dot { flex: none; width: 11px; height: 11px; border-radius: 50%; background: #636e72; }
    #dot.on { background: #d63031; animation: pulse 1.2s ease-in-out infinite; }
    #dot.finishing { background: #fdcb6e; }
    @keyframes pulse { 50% { opacity: .35; } }
    @media (prefers-reduced-motion: reduce) { #dot.on { animation: none; } }
    #meter { flex: 1 1 120px; max-width: 280px; height: 10px; background: #181a20; border: 1px solid #373b45; border-radius: 5px; overflow: hidden; }
    #meterFill { width: 0; height: 100%; background: #00a884; transition: width .12s linear; }
    #status { min-height: 1.3em; color: #aeb3bd; font-size: 13px; line-height: 1.35; overflow-wrap: anywhere; }
    .notice { background: #2b2614; border: 1px solid #6b5a1a; color: #f3e2a9; border-radius: 7px; padding: 8px 10px; font-size: 13px; line-height: 1.35; }
    #pinBar { display: grid; gap: 8px; background: #181a20; border: 1px solid #373b45; border-radius: 7px; padding: 10px; }
    #pinHelp { margin: 0; color: #d7dae0; font-size: 13px; }
    .pinrow { display: grid; grid-template-columns: 1fr auto; gap: 6px; }
    #pin.needed { border-color: #d63031; }
    #panes { --split: 60%; display: flex; flex-direction: column; height: 78vh; height: 78dvh; min-height: 440px; }
    .pane { display: flex; flex-direction: column; min-width: 0; min-height: 0; background: #181a20; border: 1px solid #373b45; border-radius: 7px; overflow: hidden; }
    #transcriptPane { flex: 0 0 var(--split); }
    #notesPane { flex: 1 1 0; }
    .paneHead { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; padding: 6px 8px 6px 12px; border-bottom: 1px solid #373b45; }
    .paneTitle { color: #d7dae0; font-size: 12px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; }
    .meta { color: #aeb3bd; font-size: 12px; }
    .grow { flex: 1; }
    button.small { background: #2d3436; padding: 6px 10px; font-size: 12px; }
    .notesbar { padding: 6px 8px; border-bottom: 1px solid #373b45; }
    #summarize.cancel { background: #d63031; }
    #liveNotes { background: #2d3436; }
    #liveNotes.on { background: #00a884; }
    .pane textarea { flex: 1 1 auto; width: 100%; min-height: 0; margin: 0; resize: none; border: 0; border-radius: 0; outline-offset: -2px; background: transparent; color: #f5f5f5; padding: 10px 12px; font-family: inherit; font-size: var(--fs); line-height: 1.5; overflow-wrap: anywhere; }
    .pane textarea::placeholder { color: #7f8590; }
    #divider { flex: 0 0 14px; display: flex; align-items: center; justify-content: center; cursor: row-resize; touch-action: none; user-select: none; -webkit-user-select: none; }
    #divider::after { content: ""; width: 56px; height: 4px; border-radius: 2px; background: #373b45; }
    #divider:hover::after, #divider.dragging::after, #divider:focus-visible::after { background: #00a884; }
    #fontDown, #fontUp { min-width: 44px; }
    @media (min-width: 960px) {
      main { height: 100vh; height: 100dvh; }
      #panes { flex: 1 1 0; flex-direction: row; height: auto; min-height: 300px; }
      #divider { cursor: col-resize; }
      #divider::after { width: 4px; height: 56px; }
    }
  </style>
</head>
<body>
<main>
  <header>
    <a class="navlink" href="/">&#8249; Remote</a>
    <h1>Transcribe</h1>
    <span id="modelState"></span>
  </header>
  <section id="pinBar" hidden>
    <p id="pinHelp"></p>
    <div class="pinrow">
      <input id="pin" inputmode="numeric" autocomplete="off" autocapitalize="off" spellcheck="false" placeholder="PIN from the desktop app" aria-label="PIN">
      <button id="pinSave" type="button">Use PIN</button>
    </div>
  </section>
  <div id="notice" class="notice" hidden></div>
  <section class="toolbar" aria-label="Recording">
    <select id="source" aria-label="Audio source"></select>
    <button id="startStop" type="button">Start</button>
    <span class="clock"><span id="dot"></span><span id="elapsed">00:00</span></span>
    <div id="meter" role="presentation"><div id="meterFill"></div></div>
  </section>
  <div id="status" role="status" aria-live="polite"></div>
  <section id="panes">
    <div id="transcriptPane" class="pane">
      <div class="paneHead">
        <span class="paneTitle">Transcript</span>
        <span id="words" class="meta">0 words</span>
        <span id="editHint" class="meta"></span>
        <span class="grow"></span>
        <button id="copyTranscript" class="small" type="button">Copy transcript</button>
      </div>
      <textarea id="transcript" spellcheck="false" aria-label="Transcript" placeholder="Press Start. Each pause in the speech adds a line such as [03:12] with the time since the session started."></textarea>
    </div>
    <div id="divider" role="separator" tabindex="0" aria-label="Resize the transcript and notes" aria-valuemin="15" aria-valuemax="85" title="Drag to resize"></div>
    <div id="notesPane" class="pane">
      <div class="paneHead">
        <span id="notesTitle" class="paneTitle">Notes</span>
        <span id="notesUpdated" class="meta"></span>
        <span class="grow"></span>
        <button id="copyNotes" class="small" type="button">Copy notes</button>
      </div>
      <div class="toolbar notesbar">
        <select id="style" aria-label="Notes style"></select>
        <button id="summarize" type="button">Summarize</button>
        <button id="liveNotes" type="button" aria-pressed="false" title="While recording, every few minutes the chat model writes detailed notes on what was said since the last update as a new section under NOTES BY TIME, and refreshes the summary at the top. Stop then writes a full summary in the chosen style.">Live notes Off</button>
        <select id="interval" aria-label="Live notes interval" title="How often live notes are updated while recording. Re-summarize all also uses it as the length of each section."></select>
      </div>
      <textarea id="notes" aria-label="Notes" placeholder="Turn on Live notes to have notes by time written while you record, or press Summarize after recording."></textarea>
    </div>
  </section>
  <section class="toolbar" aria-label="Output">
    <button id="download" class="secondary" type="button">Download .md</button>
    <button id="savePc" class="secondary" type="button">Save on PC</button>
    <button id="sendChat" type="button">Send to chat</button>
    <button id="clear" class="secondary" type="button">Clear</button>
    <span class="grow"></span>
    <button id="fontDown" class="secondary" type="button" aria-label="Smaller text">A-</button>
    <button id="fontUp" class="secondary" type="button" aria-label="Larger text">A+</button>
  </section>
</main>
<script nonce="__NONCE__">
'use strict';
const CFG = __TRANSCRIBER_CONFIG__;
const API = CFG.api;
const PIN_HELP = 'Enter the PIN shown in the desktop app under Settings > Phone Remote.';
const SETTINGS_KEY = 'voicechatbot-transcriber-settings';
const SESSION_KEY = 'voicechatbot-transcriber-session';
const MIN_FONT = 12, MAX_FONT = 40, DEFAULT_FONT = 18;
const MIN_SPLIT = 15, MAX_SPLIT = 85, DEFAULT_SPLIT = 60;
const STATUS_MESSAGE_MS = 6000;
const CHUNK_TIMEOUT_MS = 120000;
const JOB_POLL_TIMEOUT_MS = 20000;  // one poll of a notes job on the PC; a longer one counts as the PC not answering
const JOB_POLL_FAILURES_MAX = 60;   // polls in a row the PC may miss (about 3 minutes) before the page stops waiting
const JOB_STALL_MS = 120000;        // no new progress for this long: the status says the PC is still waiting
const UNREACHABLE_RETRY_MAX_MS = 10000; // while the PC cannot be reached, a chunk is retried at most this far apart
const NO_AUDIO_SHARED = 'No audio was shared. Press Start again, choose the tab (or Entire screen) and turn on "Share tab audio" (or "Share system audio") before you press Share.';

// ==================== Text helpers (same rules as Core/LiveTranscriptText) ====================

const TIMESTAMP_PREFIX = /^[ \t]*\[\d{1,3}:\d{2}(?::\d{2})?\][ \t]?/gm;
const WORD = /[\p{L}\p{M}\p{N}\p{Pc}']+/gu;
const HAS_WORD_CHAR = /[^']/;
const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

function clamp(value, min, max) { return Math.min(max, Math.max(min, value)); }
function two(n) { return String(n).padStart(2, '0'); }
function sleep(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

// "03:07" under an hour, "1:02:03" from an hour on.
function formatTimestamp(ms) {
  const total = Math.floor(Math.max(0, Number(ms) || 0) / 1000);
  const h = Math.floor(total / 3600), m = Math.floor(total / 60) % 60, s = total % 60;
  return h > 0 ? h + ':' + two(m) + ':' + two(s) : two(m) + ':' + two(s);
}

function formatLine(ms, text) {
  const clean = String(text || '').replace(/\s+/g, ' ').trim();
  return clean ? '[' + formatTimestamp(ms) + '] ' + clean : '';
}

// Spoken words; the "[mm:ss]" prefixes are not counted.
function countWords(text) {
  const words = String(text || '').replace(TIMESTAMP_PREFIX, '').match(WORD);
  if (!words) return 0;
  let count = 0;
  for (const word of words) if (HAS_WORD_CHAR.test(word)) count++;
  return count;
}

function normalizeStyle(name) {
  const wanted = String(name || '').trim().toLowerCase();
  return CFG.styles.find(style => style.toLowerCase() === wanted) || CFG.defaultStyle;
}

// The nearest interval choice (5, 10 or 15 minutes, so a stored 2 becomes 5); the default for missing values.
function normalizeInterval(minutes) {
  const m = Number(minutes);
  if (!(m > 0)) return CFG.defaultInterval;
  return CFG.intervals.slice().sort((a, b) => Math.abs(a - m) - Math.abs(b - m) || a - b)[0];
}

function shorten(text, max) {
  const line = String(text || '').replace(/\s+/g, ' ').trim();
  return line.length <= max ? line : line.slice(0, max - 3).trimEnd() + '...';
}

function chunksText(n) { return n === 1 ? '1 chunk' : n + ' chunks'; }
function intervalText(minutes) { return minutes === 1 ? 'minute' : minutes + ' minutes'; }

// -60 dBFS .. 0 dBFS as 0..100.
function levelPercent(rms) { return rms <= 0 ? 0 : clamp((20 * Math.log10(rms) + 60) / 60 * 100, 0, 100); }

function formatUpdated(date) {
  return 'Notes updated ' + date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
}

function sameText(a, b) {
  return String(a || '').replace(/\r\n/g, '\n').trim() === String(b || '').replace(/\r\n/g, '\n').trim();
}

// ==================== Notes layout (same rules as Core/TranscriptNotes) ====================
// SUMMARY SO FAR (or the style's heading after Stop / Re-summarize all), the summary, then NOTES BY TIME with one
// "[00:00–05:12]" (or "[Part 2]") section per update. The PC writes the notes; the page only needs to tell
// whether they have sections and whether the top is already a full summary in the chosen style.

const SECTION_HEADER = /^\s*\[\s*(?:\d{1,3}:\d{2}(?::\d{2})?\s*[\u2013\u2014-]\s*\d{1,3}:\d{2}(?::\d{2})?|Part\s+\d{1,4})\s*\]\s*$/i;
const KNOWN_HEADINGS = [CFG.notes.summarySoFarHeading].concat(CFG.notes.styleHeadings);

function headingText(line) { return String(line || '').trim().replace(/:+$/, '').trim().toLowerCase(); }
// Only a heading as the app writes it (whole line, same case): "Meeting notes:" a user typed is user text.
function knownHeading(line) {
  const text = String(line || '').trim();
  return KNOWN_HEADINGS.find(heading => heading === text) || null;
}
function isNotesByTimeLine(line) { return headingText(line) === CFG.notes.notesByTimeHeading.toLowerCase(); }
function styleHeading(style) { return CFG.notes.styleHeadings[CFG.styles.indexOf(normalizeStyle(style))] || ''; }

// { structured, empty, heading, top, hasSections }. Text without the layout (older notes, or notes a user typed)
// is not structured; the PC keeps it verbatim.
function notesShape(text) {
  const lines = String(text || '').replace(/\r\n?/g, '\n').split('\n');
  const byTime = lines.findIndex(isNotesByTimeLine);
  const topEnd = byTime >= 0 ? byTime : lines.length;
  let first = 0;
  while (first < topEnd && !lines[first].trim()) first++;
  const heading = first < topEnd ? knownHeading(lines[first]) : null;
  if (byTime < 0 && heading === null) {
    const whole = lines.join('\n').trim();
    return { structured: !whole, empty: !whole, heading: null, top: whole, hasSections: false };
  }
  const top = lines.slice(heading !== null ? first + 1 : first, topEnd).join('\n').trim();
  const rest = byTime >= 0 ? lines.slice(byTime + 1) : [];
  const hasSections = rest.some(line => SECTION_HEADER.test(line));
  const empty = heading === null && !top && !rest.some(line => line.trim());
  return { structured: true, empty, heading, top, hasSections };
}

// The top is a full summary in this style (written on Stop or by Re-summarize all).
function hasFullSummary(shape, style) { return shape.top.length > 0 && shape.heading === styleHeading(style); }

// The heading of the notes in the saved document: "Notes" for the layout (it carries its own headings).
function documentHeading(notes, style) {
  const shape = notesShape(notes);
  return shape.structured && !shape.empty ? 'Notes' : normalizeStyle(style);
}

function documentLines(text) {
  return String(text || '').replace(/\r\n/g, '\n').split('\n')
    .map(line => line.replace(/\s+$/, ''))
    .filter(line => line.trim().length > 0);
}

// The saved document, as LiveTranscriptText.BuildDocument writes it on the PC.
function buildDocument(doc) {
  const d = doc.date;
  let details = DAYS[d.getDay()] + ', ' + d.getDate() + ' ' + MONTHS[d.getMonth()] + ' ' + d.getFullYear() + ' ' + two(d.getHours()) + ':' + two(d.getMinutes());
  if (doc.lengthMs > 0) details += ' · ' + formatTimestamp(doc.lengthMs) + ' recorded';
  let out = '# ' + doc.title + '\n\n_' + details + '_\n\n';
  const notes = documentLines(doc.notes);
  if (notes.length) out += '## ' + (String(doc.notesHeading || '').trim() || 'Summary') + '\n\n' + notes.join('\n\n') + '\n\n';
  const lines = documentLines(doc.transcript);
  out += '## Transcript\n\n' + (lines.length ? lines.join('\n\n') : '(empty)') + '\n';
  return out;
}

function exportFileName(d) {
  return 'transcript_' + d.getFullYear() + two(d.getMonth() + 1) + two(d.getDate()) + '_' + two(d.getHours()) + two(d.getMinutes()) + '.md';
}

// ==================== Live notes timing (same rules as Core/LiveNotesPolicy) ====================

class LiveNotesPolicy {
  constructor() {
    this.enabled = false;
    this.intervalMs = CFG.defaultInterval * 60000;
    this.minNewWords = CFG.minNewWords;
    this.processed = '';   // the transcript text the current notes cover
    this.clockStart = 0;
    this.generation = 0;
    this.running = null;
  }

  restartClock(now) { this.clockStart = now; }

  // The text added since the notes were last updated. After an edit that changed the covered text, it starts
  // at the line where the covered text ended (a few words may be sent twice; none are skipped).
  getNewText(transcript) {
    const text = transcript || '';
    if (this.processed.length === 0) return text.trim();
    if (text.startsWith(this.processed)) return text.slice(this.processed.length).trim();
    const offset = Math.min(this.processed.length, text.length);
    const lineStart = offset === 0 ? 0 : text.lastIndexOf('\n', offset - 1) + 1;
    return text.slice(lineStart).trim();
  }

  countNewWords(transcript) { return countWords(this.getNewText(transcript)); }

  check(now, recording, summaryRunning, transcript) {
    if (!this.enabled) return 'Disabled';
    if (!recording) return 'NotRecording';
    if (this.running) return 'AlreadyRunning';
    if (summaryRunning) return 'SummaryRunning';
    if (now - this.clockStart < this.intervalMs) return 'WaitingForInterval';
    if (this.countNewWords(transcript) < Math.max(1, this.minNewWords)) return 'TooFewNewWords';
    return 'Due';
  }

  // After Stop: due when live notes are on or the notes already have sections, the transcript has words, and
  // either words were added or the top is not yet a full summary in the style. Notes without the layout (text a
  // user wrote) are only due with new words, since their text is kept.
  checkFinal(summaryRunning, transcript, notes, style) {
    const shape = notesShape(notes);
    if (!this.enabled && !shape.hasSections) return 'Disabled';
    if (this.running) return 'AlreadyRunning';
    if (summaryRunning) return 'SummaryRunning';
    if (countWords(transcript) === 0) return 'TooFewNewWords';
    if (this.countNewWords(transcript) === 0 && (!shape.structured || hasFullSummary(shape, style))) return 'TooFewNewWords';
    return 'Due';
  }

  // "Update notes now": whatever the interval, the Live notes switch or the 40-word minimum; needs one new word.
  checkNow(summaryRunning, transcript) {
    if (this.running) return 'AlreadyRunning';
    if (summaryRunning) return 'SummaryRunning';
    if (this.countNewWords(transcript) === 0) return 'TooFewNewWords';
    return 'Due';
  }

  tryBegin(now, recording, summaryRunning, transcript) {
    return this.check(now, recording, summaryRunning, transcript) === 'Due' ? this.begin(transcript, false) : null;
  }

  tryBeginFinal(summaryRunning, transcript, notes, style) {
    return this.checkFinal(summaryRunning, transcript, notes, style) === 'Due' ? this.begin(transcript, true) : null;
  }

  tryBeginNow(summaryRunning, transcript) {
    return this.checkNow(summaryRunning, transcript) === 'Due' ? this.begin(transcript, false) : null;
  }

  isCurrent(ticket) { return this.running === ticket && ticket.generation === this.generation; }

  complete(ticket, now) {
    if (!this.isCurrent(ticket)) return false;
    this.processed = ticket.transcript;
    this.clockStart = now;
    this.running = null;
    return true;
  }

  fail(ticket, now) {
    if (!this.isCurrent(ticket)) return;
    this.clockStart = now;
    this.running = null;
  }

  abandon(ticket) { if (this.isCurrent(ticket)) this.running = null; }

  markSummarized(transcript, now) { this.processed = transcript || ''; this.clockStart = now; }

  // A restored session: the notes cover the first processedLength characters of the transcript.
  restore(transcript, processedLength) {
    const text = transcript || '';
    this.processed = text.slice(0, clamp(Math.floor(Number(processedLength) || 0), 0, text.length));
  }

  reset(now) { this.generation++; this.running = null; this.processed = ''; this.clockStart = now; }

  begin(transcript, isFinal) {
    const text = transcript || '';
    const newText = this.getNewText(text);
    this.running = { generation: this.generation, transcript: text, newText, newWords: countWords(newText), isFinal };
    return this.running;
  }
}

// ==================== Audio: chunking at pauses (same rules as Core/SpeechChunker) ====================

function rmsOf(samples) {
  if (samples.length === 0) return 0;
  let sum = 0;
  for (let i = 0; i < samples.length; i++) sum += samples[i] * samples[i];
  return Math.sqrt(sum / samples.length);
}

// Cuts 16 kHz mono audio (floats) into chunks: after a pause once a chunk is long enough, at the maximum length
// at the latest (after the quietest frame of its last stretch). Silence before speech is dropped apart from a
// short pre-roll, and chunks with too little speech are skipped.
class SpeechChunker {
  constructor(o) {
    const framesFor = ms => Math.ceil(Math.max(0, ms) / o.frameMs - 1e-9);
    this.rate = o.sampleRate;
    this.frameSamples = Math.max(1, Math.round(o.sampleRate * o.frameMs / 1000));
    this.threshold = o.threshold;
    this.preRollFrames = framesFor(o.preRollMs);
    this.pauseFrames = Math.max(1, framesFor(o.pauseMs));
    this.minChunkFrames = framesFor(o.minChunkMs);
    this.maxChunkFrames = Math.max(2, framesFor(o.maxChunkMs));
    this.minSpeechFrames = Math.max(1, framesFor(o.minSpeechMs));
    this.cutSearchFrames = clamp(framesFor(o.cutSearchMs), 1, this.maxChunkFrames - 1);
    this.frames = [];
    this.partial = new Float32Array(this.frameSamples);
    this.partialLength = 0;
    this.nextStart = 0;
    this.speechFrames = 0;
    this.trailingSilence = 0;
    this.lastLevel = 0;
  }

  // How far into the stream the audio added so far reaches.
  get positionMs() { return (this.nextStart + this.partialLength) * 1000 / this.rate; }

  add(samples) {
    const completed = [];
    let offset = 0;
    while (offset < samples.length) {
      const take = Math.min(this.frameSamples - this.partialLength, samples.length - offset);
      this.partial.set(samples.subarray(offset, offset + take), this.partialLength);
      this.partialLength += take;
      offset += take;
      if (this.partialLength < this.frameSamples) break;
      const frame = this.partial.slice();
      this.partialLength = 0;
      const chunk = this.addFrame(frame);
      if (chunk) completed.push(chunk);
    }
    return completed;
  }

  // Ends the current chunk now: everything buffered when it holds enough speech, otherwise null.
  flush() {
    if (this.partialLength > 0) {
      const tail = this.partial.slice(0, this.partialLength);
      const rms = rmsOf(tail);
      const speech = rms >= this.threshold;
      this.frames.push({ pcm: tail, start: this.nextStart, speech, rms });
      if (speech) this.speechFrames++;
      this.nextStart += this.partialLength;
      this.partialLength = 0;
    }
    return this.take(this.frames.length);
  }

  addFrame(pcm) {
    const rms = rmsOf(pcm);
    this.lastLevel = rms;
    const speech = rms >= this.threshold;
    this.frames.push({ pcm, start: this.nextStart, speech, rms });
    this.nextStart += pcm.length;
    if (speech) { this.speechFrames++; this.trailingSilence = 0; } else this.trailingSilence++;

    if (this.speechFrames === 0) {
      const extra = this.frames.length - this.preRollFrames;
      if (extra > 0) this.frames.splice(0, extra);
      this.trailingSilence = 0;
      return null;
    }
    if (this.trailingSilence >= this.pauseFrames && this.frames.length >= this.minChunkFrames) return this.take(this.frames.length);
    if (this.frames.length >= this.maxChunkFrames) return this.take(this.quietestCutPoint());
    return null;
  }

  quietestCutPoint() {
    let best = this.frames.length - 1;
    for (let i = this.frames.length - 1; i >= this.frames.length - this.cutSearchFrames; i--) {
      if (this.frames[i].rms < this.frames[best].rms) best = i;
    }
    return best + 1;
  }

  take(count) {
    if (count <= 0 || this.frames.length === 0) return null;
    const taken = this.frames.splice(0, count);
    this.speechFrames = 0;
    this.trailingSilence = 0;
    for (const f of this.frames) { if (f.speech) { this.speechFrames++; this.trailingSilence = 0; } else this.trailingSilence++; }

    let speech = 0, length = 0;
    for (const f of taken) { length += f.pcm.length; if (f.speech) speech++; }
    if (speech < this.minSpeechFrames) return null;

    const pcm = new Float32Array(length);
    let offset = 0;
    for (const f of taken) { pcm.set(f.pcm, offset); offset += f.pcm.length; }
    return { pcm, startMs: taken[0].start * 1000 / this.rate, durationMs: length * 1000 / this.rate };
  }
}

// Streaming resampler to 16 kHz: each output sample averages the input samples it covers, a simple low-pass
// that keeps most aliasing out of Whisper's input. Upsampling repeats samples.
function makeResampler(inRate, outRate) {
  const ratio = inRate / outRate;
  let rest = new Float32Array(0);
  let pos = 0;
  return input => {
    const data = new Float32Array(rest.length + input.length);
    data.set(rest);
    data.set(input, rest.length);
    const out = new Float32Array(Math.max(0, Math.floor((data.length - pos) / ratio) + 1));
    let n = 0;
    while (pos + ratio <= data.length) {
      const start = Math.floor(pos);
      const end = Math.min(data.length, Math.max(start + 1, Math.floor(pos + ratio)));
      let sum = 0;
      for (let k = start; k < end; k++) sum += data[k];
      out[n++] = sum / (end - start);
      pos += ratio;
    }
    const keep = Math.floor(pos);
    rest = data.slice(keep);
    pos -= keep;
    return out.subarray(0, n);
  };
}

function encodeWav(samples, rate) {
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const view = new DataView(buffer);
  const write = (offset, text) => { for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i)); };
  write(0, 'RIFF'); view.setUint32(4, 36 + samples.length * 2, true); write(8, 'WAVE');
  write(12, 'fmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
  view.setUint32(24, rate, true); view.setUint32(28, rate * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
  write(36, 'data'); view.setUint32(40, samples.length * 2, true);
  for (let i = 0; i < samples.length; i++) {
    const s = clamp(samples[i], -1, 1);
    view.setInt16(44 + i * 2, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  return new Blob([buffer], { type: 'audio/wav' });
}

// ==================== Page ====================

const $ = id => document.getElementById(id);
const el = {
  modelState: $('modelState'), pinBar: $('pinBar'), pinHelp: $('pinHelp'), pin: $('pin'), pinSave: $('pinSave'),
  notice: $('notice'), source: $('source'), startStop: $('startStop'), dot: $('dot'), elapsed: $('elapsed'),
  meterFill: $('meterFill'), status: $('status'), panes: $('panes'), divider: $('divider'),
  transcript: $('transcript'), words: $('words'), editHint: $('editHint'), copyTranscript: $('copyTranscript'),
  notes: $('notes'), notesUpdated: $('notesUpdated'), copyNotes: $('copyNotes'),
  style: $('style'), summarize: $('summarize'), liveNotes: $('liveNotes'), interval: $('interval'),
  download: $('download'), savePc: $('savePc'), sendChat: $('sendChat'), clear: $('clear'),
  fontDown: $('fontDown'), fontUp: $('fontUp')
};

const isPhone = /Android|iPhone|iPad|iPod|Mobile/i.test(navigator.userAgent) ||
  (navigator.maxTouchPoints > 1 && /Macintosh/i.test(navigator.userAgent));
const canShareTab = !isPhone && !!(navigator.mediaDevices && navigator.mediaDevices.getDisplayMedia);

// ---------- Browser storage (every access can throw, e.g. in private windows) ----------

function readJson(key) {
  try { const raw = localStorage.getItem(key); return raw ? JSON.parse(raw) : null; } catch { return null; }
}
function writeJson(key, value) {
  try { localStorage.setItem(key, JSON.stringify(value)); return true; } catch { return false; }
}
function removeKey(key) { try { localStorage.removeItem(key); } catch {} }

// ---------- State ----------

const policy = new LiveNotesPolicy();
const state = {
  recording: false,
  finishing: false,     // stopped; the last chunks are still being transcribed
  starting: false,
  startedAt: 0,         // Date.now() when the session started (0: no session yet)
  elapsedBaseMs: 0,     // session time recorded before the current recording run
  runStart: 0,          // performance.now() when the current run started
  sourceLabel: '',
  sessionGen: 0,        // Clear starts a new session; results for an older one are dropped
  changeVersion: 0,
  savedVersion: 0,      // changeVersion when the session was last saved on the PC
  saveId: '',           // the PC's id for this session's file
  notesStyle: CFG.defaultStyle,  // the style the notes were last written in
  notesUpdatedAt: null,
  liveNotesFailed: false,      // the last update added nothing (the previous notes are kept)
  summaryNotRefreshed: false,  // the last update added a section but could not refresh the summary at the top
  notesProblem: ''      // why the summary at the top was not refreshed by the last update
};
const settings = { fontSize: DEFAULT_FONT, liveNotes: false, intervalMinutes: CFG.defaultInterval, style: CFG.defaultStyle, source: 'mic', split: DEFAULT_SPLIT };
const queue = [];          // recorded chunks waiting to be transcribed, oldest first (and Clear's mark, see clearSession)
const queueWaiters = [];
let pinValue = '';
let authBlocked = false;   // a request was refused for the PIN: chunks wait until it is accepted
let unreachable = false;   // the PC could not be reached: chunks wait and are retried until it answers
let clearPending = false;  // Clear was pressed: it runs once the chunks recorded before it are transcribed
let capture = null, chunker = null, timelineOriginMs = 0, peakLevel = 0;
// summaryJob: Summarize / Re-summarize all on the PC; liveJob: a live-notes update or the notes on Stop.
let pumping = false, summaryJob = null, liveJob = null, stopPromise = null;
let finalAfterSummary = false; // Stop came while Summarize ran: the final notes run once it ends
let liveTimer = 0, statusUntil = 0, persistTimer = 0, persistWarned = false, wordsTimer = 0;
let wakeLock = null, wakeLockPending = false;
let saveChain = Promise.resolve();
let dragging = false;

function sessionElapsedMs() { return state.elapsedBaseMs + (state.recording ? performance.now() - state.runStart : 0); }
function hasUnsaved() { return state.changeVersion !== state.savedVersion; }
function markChanged() { state.changeVersion++; persistSoon(); }

// While recording, a message shows for a few seconds and then the live status returns, unless sticky.
// When stopped every message stays until the next one.
function setStatus(message, sticky) {
  el.status.textContent = message;
  statusUntil = sticky ? Infinity : Date.now() + STATUS_MESSAGE_MS;
}

// ---------- PIN and requests ----------

class RequestError extends Error {
  constructor(message, status) { super(message); this.status = status; }
}
class CancelledError extends Error {}

function loadPin() {
  try { pinValue = (localStorage.getItem(CFG.pinStorageKey) || '').trim(); } catch { pinValue = ''; }
}
function storePin(value) {
  pinValue = String(value || '').trim();
  try {
    if (pinValue) localStorage.setItem(CFG.pinStorageKey, pinValue);
    else localStorage.removeItem(CFG.pinStorageKey);
  } catch {}
}
function showPinBar(message) {
  el.pinHelp.textContent = message || PIN_HELP;
  el.pinBar.hidden = false;
  el.pin.classList.add('needed');
}
function hidePinBar() {
  el.pinBar.hidden = true;
  el.pin.classList.remove('needed');
}
function blockForPin(message) {
  authBlocked = true;
  showPinBar(message);
}

// Sends one request with the PIN. Throws RequestError (status 0: the PC could not be reached) or AbortError.
async function api(path, options) {
  const o = options || {};
  if (!pinValue) {
    blockForPin(PIN_HELP);
    throw new RequestError('PIN needed. ' + PIN_HELP, 401);
  }
  const init = { method: o.method || 'GET', headers: { 'X-Phone-Remote-Pin': pinValue }, signal: o.signal, cache: 'no-store' };
  if (o.json !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(o.json);
  } else if (o.form) {
    init.body = o.form;
  }

  let response;
  try {
    response = await fetch(path, init);
  } catch (e) {
    if (e && e.name === 'AbortError') throw e;
    throw new RequestError('Cannot reach Voice Chatbot Mini on the PC. Check that it is running and that this device is on the same network.', 0);
  }

  let data = null;
  try { data = await response.json(); } catch (e) { if (e && e.name === 'AbortError') throw e; }
  if (response.ok) return data || {};

  const message = data && typeof data.error === 'string' ? data.error : '';
  if (response.status === 401) {
    storePin('');
    blockForPin('Wrong PIN. ' + PIN_HELP);
    throw new RequestError(message || 'Wrong PIN.', 401);
  }
  if (response.status === 429) {
    blockForPin(message || 'Too many wrong PIN attempts. Wait 10 minutes, then try again.');
    throw new RequestError(message || 'Too many wrong PIN attempts.', 429);
  }
  if (response.status === 413) throw new RequestError(message || 'That is too large to send to the PC.', 413);
  throw new RequestError(message || ('The PC answered with error ' + response.status + '.'), response.status);
}

function showModel(data) {
  const model = data.activeModel || '', endpoint = data.activeEndpoint || '', provider = data.activeProvider || '';
  if (!model && !endpoint && !provider) return;
  const parts = [];
  if (model) parts.push(model);
  if (endpoint) parts.push(endpoint.replace(/^https?:\/\//, ''));
  el.modelState.textContent = parts.join(' @ ');
  el.modelState.title = [provider, model, endpoint].filter(Boolean).join(' | ');
}

// Checks the PIN with the PC; when it is right, chunks held back for it are sent.
async function checkPin(announce) {
  if (!pinValue) {
    showPinBar();
    return false;
  }
  try {
    const data = await api('/api/status');
    if (!data.authorized) {
      blockForPin(PIN_HELP);
      return false;
    }
    authBlocked = false;
    hidePinBar();
    showModel(data);
    if (announce) setStatus('PIN accepted.' + (queue.length ? ' Sending the waiting chunks...' : ''));
    pumpQueue();
    return true;
  } catch (e) {
    if (e.status !== 401 && e.status !== 429) setStatus(e.message, true);
    return false;
  }
}

// An empty box retries the remembered PIN (after a lockout has ended).
async function submitPin() {
  const value = el.pin.value.trim() || pinValue;
  if (!value) {
    el.pin.focus();
    return;
  }
  storePin(value);
  el.pin.value = '';
  await checkPin(true);
}

// ---------- Settings (this browser only) ----------

function loadSettings() {
  const saved = readJson(SETTINGS_KEY);
  if (saved && typeof saved === 'object') {
    if (Number.isFinite(saved.fontSize)) settings.fontSize = saved.fontSize;
    settings.liveNotes = saved.liveNotes === true;
    settings.intervalMinutes = saved.intervalMinutes;
    if (typeof saved.style === 'string') settings.style = saved.style;
    if (saved.source === 'mic' || saved.source === 'tab') settings.source = saved.source;
    if (Number.isFinite(saved.split)) settings.split = saved.split;
  }
  settings.fontSize = clamp(Math.round(settings.fontSize), MIN_FONT, MAX_FONT);
  const storedInterval = settings.intervalMinutes;
  settings.intervalMinutes = normalizeInterval(settings.intervalMinutes);
  settings.style = normalizeStyle(settings.style);
  settings.split = clamp(settings.split, MIN_SPLIT, MAX_SPLIT);
  if (settings.source === 'tab' && !canShareTab) settings.source = 'mic';
  if (saved && storedInterval !== settings.intervalMinutes) saveSettings(); // e.g. a stored 2 is 5 from now on
}

function saveSettings() { writeJson(SETTINGS_KEY, settings); }

function applySettings() {
  el.source.add(new Option('Microphone', 'mic'));
  if (canShareTab) el.source.add(new Option('Tab / system audio', 'tab'));
  el.source.value = settings.source;
  for (const style of CFG.styles) el.style.add(new Option(style, style));
  el.style.value = settings.style;
  for (const minutes of CFG.intervals) el.interval.add(new Option(minutes + ' min', String(minutes)));
  el.interval.value = String(settings.intervalMinutes);
  policy.enabled = settings.liveNotes;
  policy.intervalMs = settings.intervalMinutes * 60000;
  applyFontSize();
  applySplit();
}

function setFontSize(size) {
  settings.fontSize = clamp(Math.round(size), MIN_FONT, MAX_FONT);
  applyFontSize();
  saveSettings();
}

function applyFontSize() {
  document.documentElement.style.setProperty('--fs', settings.fontSize + 'px');
  el.fontDown.disabled = settings.fontSize <= MIN_FONT;
  el.fontUp.disabled = settings.fontSize >= MAX_FONT;
}

function isSideBySide() { return getComputedStyle(el.panes).flexDirection === 'row'; }

function applySplit() {
  el.panes.style.setProperty('--split', settings.split.toFixed(1) + '%');
  el.divider.setAttribute('aria-valuenow', String(Math.round(settings.split)));
  el.divider.setAttribute('aria-orientation', isSideBySide() ? 'vertical' : 'horizontal');
}

// ---------- Session backup (survives a reload in this browser) ----------

function persistSoon() {
  if (!persistTimer) persistTimer = setTimeout(persistNow, state.recording ? 2000 : 700);
}

function persistNow() {
  if (persistTimer) {
    clearTimeout(persistTimer);
    persistTimer = 0;
  }
  const transcript = el.transcript.value, notes = el.notes.value;
  if (!transcript.trim() && !notes.trim()) {
    removeKey(SESSION_KEY);
    return;
  }
  const ok = writeJson(SESSION_KEY, {
    v: 1,
    transcript,
    notes,
    notesStyle: state.notesStyle,
    notesUpdatedAt: state.notesUpdatedAt ? state.notesUpdatedAt.getTime() : 0,
    startedAt: state.startedAt,
    elapsedMs: Math.round(sessionElapsedMs()),  // recording continues from here after a reload
    saveId: state.saveId,
    unsaved: hasUnsaved(),
    processedLength: policy.processed.length,   // how much of the transcript the notes cover
    savedAt: Date.now()
  });
  if (ok) {
    persistWarned = false;
  } else if (!persistWarned) {
    persistWarned = true;
    setStatus('This browser could not update its backup copy of the transcript (storage is full or blocked), so a reload could lose the latest text. Use Save on PC or Download .md to keep it.', true);
  }
}

// The session kept before the page was reloaded or closed: transcript, notes, the time recorded so far and the
// live-notes progress. Returns when it is from (a Date), or null when there is nothing to restore.
function restoreSession() {
  const saved = readJson(SESSION_KEY);
  if (!saved || typeof saved !== 'object') return null;
  const transcript = typeof saved.transcript === 'string' ? saved.transcript : '';
  const notes = typeof saved.notes === 'string' ? saved.notes : '';
  if (!transcript.trim() && !notes.trim()) return null;

  el.transcript.value = transcript;
  el.notes.value = notes;
  state.notesStyle = normalizeStyle(saved.notesStyle);
  state.notesUpdatedAt = Number(saved.notesUpdatedAt) > 0 ? new Date(Number(saved.notesUpdatedAt)) : null;
  state.startedAt = Number(saved.startedAt) > 0 ? Number(saved.startedAt) : 0;
  state.elapsedBaseMs = clamp(Number(saved.elapsedMs) || 0, 0, 30 * 86400000);
  state.saveId = typeof saved.saveId === 'string' && /^[0-9a-f]{32}$/.test(saved.saveId) ? saved.saveId : '';
  policy.restore(transcript, saved.processedLength);
  state.changeVersion = 1;
  state.savedVersion = saved.unsaved === false ? 1 : 0;
  const from = state.startedAt || Number(saved.savedAt) || 0;
  return new Date(from > 0 ? from : Date.now());
}

// ---------- Display ----------

function updateWords() {
  if (wordsTimer) {
    clearTimeout(wordsTimer);
    wordsTimer = 0;
  }
  const n = countWords(el.transcript.value);
  el.words.textContent = n === 1 ? '1 word' : n.toLocaleString() + ' words';
}

function updateWordsSoon() {
  if (!wordsTimer) wordsTimer = setTimeout(updateWords, 400);
}

function updateUi() {
  const rec = state.recording, fin = state.finishing;
  el.startStop.textContent = fin ? 'Finishing...' : rec ? 'Stop' : state.starting ? 'Starting...' : 'Start';
  el.startStop.classList.toggle('recording', rec);
  el.startStop.classList.toggle('finishing', fin);
  el.startStop.disabled = fin || state.starting;
  el.dot.className = rec ? 'on' : fin ? 'finishing' : '';
  el.elapsed.textContent = formatTimestamp(sessionElapsedMs());
  if (!rec) el.meterFill.style.width = '0%';
  // The transcript can be corrected while stopped; while recording new lines keep arriving.
  el.transcript.readOnly = rec || fin;
  el.editHint.textContent = rec || fin ? 'Read-only while recording' : 'Editable';
  el.source.disabled = rec || fin || state.starting;
  el.clear.disabled = fin || clearPending;
  updateNotesControls();
}

// The notes are read-only while recording and while notes are being written (updates only add to them then);
// editable when stopped. The button reads "Update notes now" while recording, "Summarize" for empty notes,
// "Re-summarize all" otherwise, and "Cancel" while a summary is being written.
function updateNotesControls() {
  const rec = state.recording;
  const stopping = !rec && (state.finishing || !!stopPromise);
  el.notes.readOnly = rec || stopping || !!liveJob || !!summaryJob;
  let label, tip;
  if (summaryJob) {
    label = 'Cancel';
    tip = 'Stop writing the notes; the previous notes are kept';
  } else if (rec) {
    label = 'Update notes now';
    tip = 'Add notes on what was said since the last update now, without waiting for the live-notes interval';
  } else if (!el.notes.value.trim()) {
    label = 'Summarize';
    tip = 'Write notes by time and a summary in the chosen style from the whole transcript; a long transcript is written section by section';
  } else {
    label = 'Re-summarize all';
    tip = 'Replace the notes with fresh notes by time and a summary in the chosen style, written from the whole transcript';
  }
  if (el.summarize.textContent !== label) el.summarize.textContent = label;
  el.summarize.title = tip;
  el.summarize.classList.toggle('cancel', !!summaryJob);
  // While an update or the notes on Stop are being written, wait for them.
  el.summarize.disabled = !summaryJob && (stopping || !!liveJob);
  el.style.disabled = !!summaryJob;
}

function updateLiveNotesUi() {
  el.liveNotes.textContent = policy.enabled ? 'Live notes On' : 'Live notes Off';
  el.liveNotes.classList.toggle('on', policy.enabled);
  el.liveNotes.setAttribute('aria-pressed', String(policy.enabled));
}

function updateNotesHeader() {
  const hasNotes = el.notes.value.trim().length > 0;
  const updated = hasNotes && state.notesUpdatedAt ? formatUpdated(state.notesUpdatedAt) : '';
  let detail = updated;
  if (summaryJob) detail = summaryJob.progress || 'Summarizing...';
  else if (liveJob) detail = liveJob.startedAt ? 'Updating notes... ' + jobClock(liveJob) : 'Updating notes...';
  else if (state.liveNotesFailed) detail = updated ? 'Notes update failed · ' + updated : 'Notes update failed';
  else if (state.summaryNotRefreshed) detail = updated ? 'Summary not refreshed · ' + updated : 'Summary not refreshed';
  el.notesUpdated.textContent = detail;
  el.notesUpdated.title = state.notesProblem && !summaryJob && !liveJob ? state.notesProblem : '';
}

// Every 200 ms: elapsed time, input level and the live status line.
function tick() {
  el.elapsed.textContent = formatTimestamp(sessionElapsedMs());
  const level = peakLevel;
  peakLevel = 0;
  el.meterFill.style.width = (state.recording ? levelPercent(level) : 0).toFixed(0) + '%';
  if (Date.now() < statusUntil || (!state.recording && !state.finishing)) return;

  const pending = pendingChunks();
  let status;
  if (authBlocked && pending > 0) status = 'PIN needed: ' + chunksText(pending) + ' waiting. Enter the PIN above to send them.';
  else if (unreachable && pending > 0) status = 'PC unreachable: ' + chunksText(pending) + ' waiting. They are sent once the PC answers again.';
  else if (!state.recording) status = pending > 0 ? 'Finishing... ' + chunksText(pending) + ' left to transcribe.' : 'Finishing...';
  else status = 'Listening to ' + state.sourceLabel + (pending > 0 ? ' · transcribing ' + chunksText(pending) + '...' : '');
  if (el.status.textContent !== status) el.status.textContent = status;
  statusUntil = 0;
}

// Adds a line and follows it only when the reader is already at the end, not while they scroll back.
function appendLine(line) {
  if (!line) return;
  const box = el.transcript;
  const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight <= 8;
  const top = box.scrollTop;
  const value = box.value;
  box.value = value + (value.length === 0 || value.endsWith('\n') ? '' : '\n') + line;
  box.scrollTop = atBottom ? box.scrollHeight : top;
  updateWords();
  markChanged();
}

function setNotes(text) {
  el.notes.value = text;
  updateNotesHeader();
  updateNotesControls();
  markChanged();
}

// New sections are added at the end: a reader at the end keeps following them, one who scrolled back stays put.
function replaceNotesKeepingScroll(text) {
  const box = el.notes;
  const atEnd = box.scrollHeight > box.clientHeight && box.scrollTop + box.clientHeight >= box.scrollHeight - 6;
  const top = box.scrollTop;
  setNotes(text);
  box.scrollTop = atEnd ? box.scrollHeight : top;
}

// ---------- Capture ----------

async function openCapture(kind, ctx) {
  let stream;
  if (kind === 'tab') {
    stream = await navigator.mediaDevices.getDisplayMedia({
      video: true,
      audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false },
      systemAudio: 'include',
      selfBrowserSurface: 'exclude',
      surfaceSwitching: 'include'
    });
    // Only the sound is used; the picture is never needed.
    for (const track of stream.getVideoTracks()) {
      track.stop();
      stream.removeTrack(track);
    }
    if (stream.getAudioTracks().length === 0) {
      stream.getTracks().forEach(track => track.stop());
      throw new Error(NO_AUDIO_SHARED);
    }
  } else {
    stream = await navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: false, noiseSuppression: true, autoGainControl: true, channelCount: 1 },
      video: false
    });
  }

  const track = stream.getAudioTracks()[0];
  const cap = {
    kind, stream, ctx, source: null, processor: null, sink: null,
    resample: makeResampler(ctx.sampleRate, CFG.chunk.sampleRate),
    label: kind === 'tab' ? 'the shared audio' : (track && track.label ? track.label : 'the microphone')
  };
  try {
    if (ctx.state !== 'running') await ctx.resume();
    cap.source = ctx.createMediaStreamSource(stream);
    cap.processor = ctx.createScriptProcessor(4096, 1, 1);
    // The processor only runs while connected to the output; a muted gain keeps it silent.
    cap.sink = ctx.createGain();
    cap.sink.gain.value = 0;
    cap.processor.onaudioprocess = e => onAudio(cap, e.inputBuffer.getChannelData(0));
    cap.source.connect(cap.processor);
    cap.processor.connect(cap.sink);
    cap.sink.connect(ctx.destination);
  } catch (e) {
    closeCapture(cap);
    throw e;
  }
  for (const t of stream.getAudioTracks()) t.addEventListener('ended', () => onTrackEnded(cap));
  return cap;
}

function closeCapture(cap) {
  if (!cap) return;
  if (cap.processor) cap.processor.onaudioprocess = null;
  for (const node of [cap.source, cap.processor, cap.sink]) {
    try { if (node) node.disconnect(); } catch {}
  }
  try { cap.stream.getTracks().forEach(track => track.stop()); } catch {}
  try {
    if (cap.ctx.state !== 'closed') {
      const closing = cap.ctx.close();
      if (closing && closing.catch) closing.catch(() => {});
    }
  } catch {}
}

function onAudio(cap, input) {
  if (cap !== capture || !state.recording || !chunker) return;
  const samples = cap.resample(input);
  if (samples.length === 0) return;
  for (const chunk of chunker.add(samples)) enqueue(chunk);
  if (chunker.lastLevel > peakLevel) peakLevel = chunker.lastLevel;
}

function onTrackEnded(cap) {
  if (cap !== capture || !state.recording) return;
  stopRecording(cap.kind === 'tab'
    ? 'Recording stopped: audio sharing ended.'
    : 'Recording stopped: the microphone was turned off (the screen locked, or another app took the microphone).');
}

function describeCaptureError(e, kind) {
  const name = e && e.name;
  if (e && e.message === NO_AUDIO_SHARED) return NO_AUDIO_SHARED;
  if (kind === 'tab') {
    if (name === 'NotAllowedError' || name === 'AbortError') return 'Sharing was cancelled. Press Start and choose a tab or screen, with its audio, to transcribe.';
    if (name === 'NotSupportedError' || name === 'TypeError') return 'This browser cannot share tab or system audio. Use Chrome or Edge on a PC, or choose Microphone.';
  } else {
    if (name === 'NotAllowedError' || name === 'SecurityError') return 'Microphone access was blocked. Allow the microphone for this site (the icon next to the address), then press Start again.';
    if (name === 'NotFoundError' || name === 'OverconstrainedError') return 'No microphone was found. Connect one and press Start again.';
    if (name === 'NotReadableError' || name === 'AbortError') return 'The microphone could not be opened; another app may be using it. Close that app and press Start again.';
  }
  return 'Could not start recording: ' + ((e && e.message) || 'unknown error');
}

// ---------- Transcription queue: strictly one chunk at a time, in order ----------

function enqueue(chunk) {
  queue.push({ blob: encodeWav(chunk.pcm, CFG.chunk.sampleRate), atMs: timelineOriginMs + chunk.startMs, gen: state.sessionGen });
  pumpQueue();
}

function pendingChunks() { return queue.filter(item => !item.clear).length; }

function waitForQueue() {
  return queue.length === 0 && !pumping ? Promise.resolve() : new Promise(resolve => queueWaiters.push(resolve));
}

function notifyQueueWaiters() {
  if (queue.length > 0 || pumping) return;
  while (queueWaiters.length) queueWaiters.shift()();
}

async function pumpQueue() {
  if (pumping) return;
  pumping = true;
  try {
    while (queue.length > 0 && (!authBlocked || queue[0].clear)) {
      const item = queue[0];
      if (item.clear) {
        // Every chunk recorded before Clear is in the transcript now.
        queue.shift();
        await finishClear(item);
        continue;
      }
      const outcome = await transcribeChunk(item);
      if (outcome.auth) break; // kept, and sent once the PIN is accepted
      const index = queue.indexOf(item);
      if (index >= 0) queue.splice(index, 1);
      if (item.gen !== state.sessionGen) continue; // cleared meanwhile
      if (outcome.text) appendLine(formatLine(item.atMs, outcome.text));
      else if (outcome.error) setStatus('A chunk could not be transcribed: ' + shorten(outcome.error, 140));
    }
  } finally {
    pumping = false;
    notifyQueueWaiters();
  }
}

async function transcribeChunk(item) {
  let attempt = 0, offline = 0;
  for (;;) {
    if (item.gen !== state.sessionGen) return {};
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), CHUNK_TIMEOUT_MS);
    try {
      const form = new FormData();
      form.append('audio', item.blob, 'chunk.wav');
      const data = await api(API + '/chunk', { method: 'POST', form, signal: controller.signal });
      unreachable = false;
      return { text: String(data.text || '').trim() };
    } catch (e) {
      if (e.status === 401 || e.status === 429) return { auth: true };
      if (e.status === 0) {
        // The PC cannot be reached (a Wi-Fi drop, the PC asleep): the chunk is kept and tried again until it can.
        unreachable = true;
        offline++;
        await sleep(Math.min(offline * 2000, UNREACHABLE_RETRY_MAX_MS));
        continue;
      }
      unreachable = false;
      attempt++;
      const timedOut = e.name === 'AbortError';
      const transient = timedOut || e.status === 502 || e.status === 503 || e.status === 504;
      if (!transient || attempt >= 3) return { error: timedOut ? 'the PC took too long to answer.' : e.message };
      await sleep(attempt * 2000);
    } finally {
      clearTimeout(timer);
    }
  }
}

// ---------- Recording ----------

async function startRecording() {
  if (state.recording || state.finishing || state.starting) return;
  const Ctx = window.AudioContext || window.webkitAudioContext;
  if (!window.isSecureContext || !navigator.mediaDevices || !Ctx) {
    setStatus('Recording needs the https:// address of the remote in a current browser. Open the address shown in the desktop app under Settings > Phone Remote.', true);
    return;
  }
  if (!pinValue) {
    showPinBar();
    setStatus('Enter the PIN first.', true);
    el.pin.focus();
    return;
  }

  const kind = el.source.value === 'tab' && canShareTab ? 'tab' : 'mic';
  let ctx;
  try {
    // Created during the click, so browsers that need a user gesture let it run.
    ctx = new Ctx();
    const resumed = ctx.resume();
    if (resumed && resumed.catch) resumed.catch(() => {});
  } catch (e) {
    setStatus('Could not start audio: ' + e.message, true);
    return;
  }

  state.starting = true;
  updateUi();
  setStatus(kind === 'tab'
    ? 'Choose the tab or screen to transcribe and turn on its audio sharing...'
    : 'Starting the microphone...', true);

  let cap;
  try {
    cap = await openCapture(kind, ctx);
  } catch (e) {
    try { ctx.close().catch(() => {}); } catch {}
    state.starting = false;
    updateUi();
    setStatus(describeCaptureError(e, kind), true);
    return;
  }

  state.starting = false;
  chunker = new SpeechChunker(CFG.chunk);
  timelineOriginMs = state.elapsedBaseMs; // session time at the chunker's position zero
  capture = cap;
  state.sourceLabel = cap.label;
  if (!state.startedAt) state.startedAt = Date.now();
  state.runStart = performance.now();
  state.recording = true;
  policy.restartClock(Date.now());
  clearInterval(liveTimer);
  liveTimer = setInterval(liveNotesTick, CFG.checkEveryMs);
  statusUntil = 0;
  acquireWakeLock();
  updateUi();
  persistSoon();
  checkPin(false);
  if (isPhone) setStatus('Recording. Keep this page open with the screen on: the phone stops the microphone when the screen locks.');
}

function stopRecording(reason) {
  if (!state.recording) return stopPromise || Promise.resolve();
  const stopping = stopCore(reason)
    .catch(e => setStatus('Stop failed: ' + e.message, true))
    .finally(() => {
      if (stopPromise === stopping) stopPromise = null;
      updateUi();
    });
  stopPromise = stopping;
  return stopping;
}

async function stopCore(reason) {
  state.elapsedBaseMs = sessionElapsedMs();
  state.recording = false;
  state.finishing = true;
  clearInterval(liveTimer);
  liveTimer = 0;
  const cap = capture;
  capture = null;
  if (chunker) {
    const rest = chunker.flush();
    if (rest) enqueue(rest);
  }
  chunker = null;
  closeCapture(cap);
  releaseWakeLock();
  statusUntil = 0;
  updateUi();
  tick();

  // Let the chunks already recorded finish instead of dropping them.
  await waitForQueue();
  state.finishing = false;
  updateUi();
  persistNow();

  // Saved on the PC right away, then again once the final notes are written (with Live notes on, or when the
  // notes already have sections: a last section for the words since the previous update and a full summary).
  const stopped = reason || 'Stopped.';
  const saved = await autoSave();
  if (!state.recording) setStatus(stopped + savedNote(saved), true);
  const notesUpdated = await finishLiveNotes(stopped);
  if (!notesUpdated) return;
  const resaved = await autoSave();
  persistNow();
  if (!state.recording && state.notesUpdatedAt) {
    const problem = state.notesProblem ? ' ' + state.notesProblem : '';
    setStatus(stopped + ' ' + formatUpdated(state.notesUpdatedAt) + '.' + problem + savedNote(resaved || saved), true);
  }
}

function savedNote(fileName) { return fileName ? ' Saved on the PC as ' + fileName + '.' : ''; }

async function acquireWakeLock() {
  if (!('wakeLock' in navigator) || wakeLock || wakeLockPending || document.visibilityState !== 'visible') return;
  wakeLockPending = true;
  try {
    const lock = await navigator.wakeLock.request('screen');
    if (!state.recording) {
      lock.release().catch(() => {});
      return;
    }
    wakeLock = lock;
    lock.addEventListener('release', () => { if (wakeLock === lock) wakeLock = null; });
  } catch {
    // Not allowed right now (battery saver, hidden page); the phone notice explains the risk.
  } finally {
    wakeLockPending = false;
  }
}

function releaseWakeLock() {
  const lock = wakeLock;
  wakeLock = null;
  if (lock) lock.release().catch(() => {});
}

// ---------- Saving on the PC ----------

// One save at a time, so the first save of a session gets its file before the next one uses it.
// elapsedMs: the session's length, when it is not the clock's (Clear while recording).
function saveOnPc(elapsedMs) {
  const save = () => saveOnPcNow(elapsedMs);
  const run = saveChain.then(save, save);
  saveChain = run.catch(() => {});
  return run;
}

async function saveOnPcNow(elapsedMs) {
  if (!state.startedAt) state.startedAt = Date.now();
  const version = state.changeVersion, gen = state.sessionGen;
  const data = await api(API + '/save', {
    method: 'POST',
    json: {
      transcript: el.transcript.value,
      notes: el.notes.value,
      notesStyle: state.notesStyle,
      startedAt: state.startedAt,
      elapsedMs: Math.round(typeof elapsedMs === 'number' ? elapsedMs : sessionElapsedMs()),
      saveId: state.saveId
    }
  });
  if (gen === state.sessionGen) {
    if (typeof data.saveId === 'string') state.saveId = data.saveId;
    if (state.changeVersion === version) state.savedVersion = version;
    persistSoon();
  }
  return data.fileName || '';
}

// Saves the session when the transcript changed since the last save. Returns the file name, or null.
async function autoSave() {
  if (!hasUnsaved() || !el.transcript.value.trim()) return null;
  try {
    return await saveOnPc();
  } catch (e) {
    setStatus('Could not save on the PC: ' + e.message, true);
    return null;
  }
}

// ---------- Notes jobs on the PC ----------

function cancelJobOnPc(id) {
  if (id) api(API + '/jobs/' + encodeURIComponent(id) + '/cancel', { method: 'POST' }).catch(() => {});
}

// Stops a job (summaryJob or liveJob); its result is dropped and the previous notes stay.
function cancelJob(job) {
  if (!job || job.cancelled) return;
  job.cancelled = true;
  cancelJobOnPc(job.id);
  if (job.stop) job.stop(); // a request or pause the job is waiting for ends now
}

function cancelSummary() { cancelJob(summaryJob); }
function cancelLiveNotes() { cancelJob(liveJob); }

// Waits for promise; Cancel (cancelJob) ends the wait at once with CancelledError.
function untilCancelled(job, promise) {
  return new Promise((resolve, reject) => {
    job.stop = () => reject(new CancelledError());
    if (job.cancelled) job.stop();
    promise.then(resolve, reject);
  }).finally(() => { job.stop = null; });
}

// One poll of a job. A poll the PC does not answer in time counts as the PC not answering (status 0).
function pollJob(id) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), JOB_POLL_TIMEOUT_MS);
  return api(API + '/jobs/' + encodeURIComponent(id), { signal: controller.signal })
    .catch(e => {
      if (e && e.name === 'AbortError') throw new RequestError('The PC did not answer in time.', 0);
      throw e;
    })
    .finally(() => clearTimeout(timer));
}

function noteJobProgress(job, progress) {
  const text = String(progress || '');
  if (text === job.lastProgress) return;
  job.lastProgress = text;
  job.progressAt = Date.now();
}

// "0:42" or "1:02:03": how long a notes job has run.
function formatDuration(ms) {
  const total = Math.floor(Math.max(0, Number(ms) || 0) / 1000);
  const h = Math.floor(total / 3600), m = Math.floor(total / 60) % 60, s = total % 60;
  return h > 0 ? h + ':' + two(m) + ':' + two(s) : m + ':' + two(s);
}

// "0:42 - Section 1 of 2" while a notes job runs, with a note when nothing has moved on for a while.
function jobClock(job) {
  const now = Date.now();
  const parts = [formatDuration(now - (job.startedAt || now))];
  const progress = String(job.lastProgress || '').trim().replace(/(\.\.\.|\u2026)$/, '');
  if (progress) parts.push(progress);
  if (job.failures > 0) parts.push('the PC is not answering, trying again');
  else if (now - (job.progressAt || now) >= JOB_STALL_MS) parts.push(job.id ? 'still waiting for the chat model on the PC' : 'still waiting for the PC to answer');
  return parts.join(' - ');
}

// Starts a notes job on the PC and polls it until it ends, so a phone that sleeps or loses Wi-Fi for a moment
// does not lose it (the job keeps going on the PC). Resolves to the finished job ({ result, warning });
// throws CancelledError after cancelJob, or an Error when it failed, was refused or the PC no longer knows it.
// While it runs, job.startedAt, job.lastProgress, job.progressAt and job.failures feed jobClock.
async function runJob(path, body, job, onProgress) {
  job.startedAt = job.startedAt || Date.now();
  job.progressAt = Date.now();
  job.lastProgress = '';
  job.failures = 0;
  const start = api(path, { method: 'POST', json: body });
  // After Cancel the page stops waiting at once; a job the PC still starts is then cancelled there.
  start.then(d => { if (job.cancelled) cancelJobOnPc(String((d && d.id) || '')); }, () => {});
  let data = await untilCancelled(job, start);
  job.id = String(data.id || '');
  noteJobProgress(job, data.progress);
  while (data.state === 'running' && !job.cancelled) {
    await untilCancelled(job, sleep(1000));
    try {
      data = await untilCancelled(job, pollJob(job.id));
      job.failures = 0;
    } catch (e) {
      if (job.cancelled || e instanceof CancelledError) throw new CancelledError();
      if (e.status === 0 && ++job.failures < JOB_POLL_FAILURES_MAX) {
        await untilCancelled(job, sleep(2000));
        continue;
      }
      if (e.status === 0) {
        cancelJobOnPc(job.id); // nobody waits for it any more
        throw new Error('Gave up waiting: the PC stopped answering. Check that Voice Chatbot Mini is running and this device is on the same network.');
      }
      throw e;
    }
    noteJobProgress(job, data.progress);
    if (data.progress && !job.cancelled && onProgress) onProgress(String(data.progress));
  }
  if (job.cancelled || data.state === 'cancelled') throw new CancelledError();
  if (data.state === 'failed') throw new Error(data.error || 'The request failed.');
  return data;
}

// ---------- Summarize / Re-summarize all / Update notes now ----------

// While recording: "Update notes now". When stopped: Summarize / Re-summarize all. While that runs: Cancel.
function summarizeClick() {
  if (summaryJob) {
    cancelSummary();
    return;
  }
  if (state.recording) {
    updateNotesNow();
    return;
  }
  if (state.finishing || stopPromise || liveJob) {
    setStatus('The notes are still being written. Try again in a moment.');
    return;
  }
  rebuildNotes();
}

// Adds a section right away, whatever the interval and the Live notes switch.
function updateNotesNow() {
  const check = policy.checkNow(!!summaryJob, el.transcript.value);
  if (check === 'AlreadyRunning') {
    setStatus('The notes are already being updated.');
    return;
  }
  if (check === 'TooFewNewWords') {
    setStatus('Nothing new to add to the notes yet.');
    return;
  }
  const ticket = policy.tryBeginNow(!!summaryJob, el.transcript.value);
  if (ticket) startLiveNotes(ticket);
}

// Summarize (empty notes) or Re-summarize all (asks first): notes by time for each interval of the whole
// transcript, then a full summary in the chosen style at the top. The previous notes stay until the new ones are
// ready, so a cancel or an error keeps them.
async function rebuildNotes() {
  const snapshot = el.transcript.value;
  const transcript = snapshot.trim();
  if (!transcript) {
    setStatus('No transcript to summarize yet.');
    return;
  }
  const replacing = el.notes.value.trim().length > 0;
  if (replacing && !confirm('Replace the current notes with a fresh summary of the whole transcript?')) {
    setStatus('Not re-summarized; the current notes are kept.');
    return;
  }
  // Things may have moved on while the question was open.
  if (summaryJob || liveJob || state.recording || state.finishing || stopPromise || el.transcript.value !== snapshot) return;

  const style = normalizeStyle(el.style.value);
  const gen = state.sessionGen;
  const job = { id: '', cancelled: false, progress: '', startedAt: Date.now() };
  summaryJob = job;
  updateNotesControls();
  updateNotesHeader();
  // "Writing summary... 0:42 - Section 1 of 2", updated every second while the PC writes the notes.
  const title = replacing ? 'Re-summarizing the whole transcript...' : 'Writing ' + style.toLowerCase() + '...';
  const showClock = () => { if (summaryJob === job && !job.cancelled) setStatus(title + ' ' + jobClock(job), true); };
  showClock();
  const clock = setInterval(showClock, 1000);

  let done = false;
  try {
    const data = await runJob(API + '/summarize', {
      transcript,
      style,
      intervalMinutes: settings.intervalMinutes,
      elapsedMs: Math.round(sessionElapsedMs())
    }, job, message => {
      // Section by section: "Section 3 of 12...", then "Writing the summary...".
      job.progress = message;
      showClock();
      updateNotesHeader();
    });
    if (gen !== state.sessionGen) return;

    const notes = String(data.result || '');
    if (!notes.trim()) {
      setStatus('The chat model returned no notes; the previous notes are kept.', !state.recording);
      return;
    }
    state.notesStyle = style;
    state.notesUpdatedAt = new Date();
    state.liveNotesFailed = false;
    state.summaryNotRefreshed = false;
    state.notesProblem = '';
    if (summaryJob === job) summaryJob = null;
    setNotes(notes);
    el.notes.scrollTop = 0;
    // Live notes carry on from here with the words added after it.
    policy.markSummarized(snapshot, Date.now());
    persistSoon();
    done = true;
  } catch (e) {
    if (job.cancelled) {
      if (gen === state.sessionGen) setStatus(replacing ? 'Re-summarize cancelled; the previous notes are kept.' : 'Summary cancelled.');
    } else if (e instanceof CancelledError) {
      // Not this page's Cancel: the job was stopped on the PC (the phone remote was stopped, say).
      if (gen === state.sessionGen) setStatus('The summary was stopped on the PC.' + (replacing ? ' The previous notes are kept.' : '') + ' Press ' + (replacing ? 'Re-summarize all' : 'Summarize') + ' to try again.', !state.recording);
    } else {
      // Failed, refused (409, too many running), unknown to the PC (404) or the PC stopped answering: stays shown
      // when stopped; while recording it gives way to the recording status after a while.
      setStatus((e.status === 409 ? 'Summary not started: ' : 'Summary failed: ') + e.message + (replacing ? ' The previous notes are kept.' : ''), !state.recording);
    }
  } finally {
    clearInterval(clock);
    if (summaryJob === job) summaryJob = null;
    updateNotesControls();
    updateNotesHeader();
    if (!done) finishSkippedFinalNotes();
  }
  if (!done) return;

  const saved = state.recording ? null : await autoSave();
  if (gen === state.sessionGen) setStatus(style + ' ready.' + savedNote(saved), !state.recording);
  finishSkippedFinalNotes();
}

// Recording was started and stopped while Summarize / Re-summarize all ran, so the final notes waited for it:
// they run now (a section for the words recorded meanwhile and the full summary), then the session is saved.
async function finishSkippedFinalNotes() {
  if (!finalAfterSummary || summaryJob || state.recording || state.finishing) return;
  finalAfterSummary = false;
  if (!(await finishLiveNotes('Stopped.'))) return;
  await autoSave();
  persistNow();
}

// ---------- Live notes ----------

// Every 15 seconds while recording: starts an update when one is due.
function liveNotesTick() {
  if (!state.recording) return;
  const ticket = policy.tryBegin(Date.now(), state.recording, !!summaryJob, el.transcript.value);
  if (ticket) startLiveNotes(ticket);
}

// title: for the notes on Stop, the status shown with the job's time and progress while they are written.
function startLiveNotes(ticket, title) {
  const job = { ticket, id: '', cancelled: false, promise: null, title: title || '', startedAt: Date.now() };
  liveJob = job;
  job.promise = runLiveNotes(job);
  return job.promise;
}

// A live update (or, for the final ticket, the notes on Stop), written on the PC: notes on only the text added
// since the last update become a new section, then the summary at the top is rewritten (on Stop: a full summary
// of the whole transcript). Errors keep the previous notes; when only the summary fails, the new section is kept.
// Resolves to true when new notes were shown; never rejects.
async function runLiveNotes(job) {
  const ticket = job.ticket;
  const notesBefore = el.notes.value;
  const style = normalizeStyle(el.style.value);
  const elapsedMs = Math.round(sessionElapsedMs());
  updateNotesHeader();
  updateNotesControls();
  // Every second: the time in the notes header and, for the notes on Stop, in the status (not while recording).
  const clock = setInterval(() => {
    if (liveJob !== job || job.cancelled) return;
    updateNotesHeader();
    if (job.title && !state.recording) setStatus(job.title + ' ' + jobClock(job), true);
  }, 1000);
  try {
    const data = await runJob(API + '/notes', {
      notes: notesBefore,
      newText: ticket.newText,
      transcript: ticket.isFinal ? ticket.transcript : '',
      style,
      elapsedMs,
      isFinal: ticket.isFinal
    }, job, null);
    if (job.cancelled || !policy.isCurrent(ticket)) {
      policy.abandon(ticket);
      return false; // cleared meanwhile
    }
    if (!sameText(el.notes.value, notesBefore)) {
      // The notes were edited while the update was written: keep the edit, add to it on the next check.
      policy.abandon(ticket);
      setStatus('Live notes not applied because the notes were edited meanwhile. They update on the next check.');
      return false;
    }
    const notes = String(data.result || '');
    if (!notes.trim()) throw new Error('The model returned no notes.');
    const warning = String(data.warning || '');
    policy.complete(ticket, Date.now());
    state.notesUpdatedAt = new Date();
    state.liveNotesFailed = false;
    state.summaryNotRefreshed = !!warning;
    state.notesProblem = warning;
    state.notesStyle = style;
    if (!sameText(notes, notesBefore)) replaceNotesKeepingScroll(notes);
    else markChanged();
    setStatus(formatUpdated(state.notesUpdatedAt) + '.' + (warning ? ' ' + warning : ''));
    return true;
  } catch (e) {
    if (e instanceof CancelledError || job.cancelled) {
      policy.abandon(ticket);
      return false;
    }
    const current = policy.isCurrent(ticket);
    policy.fail(ticket, Date.now());
    if (current) {
      state.liveNotesFailed = true;
      setStatus('Live notes not updated, the previous notes are kept: ' + shorten(e.message, 120));
    }
    return false;
  } finally {
    clearInterval(clock);
    if (liveJob === job) liveJob = null;
    updateNotesHeader();
    updateNotesControls();
  }
}

// After Stop (the last chunks are transcribed): waits for an update in progress, then writes the final notes when
// they are due (live notes on or notes by time there). True when either put new notes in the notes pane.
async function finishLiveNotes(stopped) {
  let updated = false;
  if (liveJob) updated = await liveJob.promise;
  const ticket = policy.tryBeginFinal(!!summaryJob, el.transcript.value, el.notes.value, normalizeStyle(el.style.value));
  // A running Summarize blocks the final notes; they run when it ends (finishSkippedFinalNotes).
  finalAfterSummary = !ticket && !!summaryJob;
  if (!ticket) return updated;
  const title = stopped + ' Writing the final notes and summary...';
  if (!state.recording) setStatus(title, true);
  return (await startLiveNotes(ticket, title)) || updated;
}

function toggleLiveNotes() {
  policy.enabled = !policy.enabled;
  settings.liveNotes = policy.enabled;
  saveSettings();
  updateLiveNotesUi();
  if (!policy.enabled) {
    cancelLiveNotes();
    setStatus('Live notes off. Update notes now still adds notes while recording, and Summarize works when stopped.');
    return;
  }
  const every = intervalText(settings.intervalMinutes);
  setStatus(state.recording
    ? 'Live notes on: every ' + every + ', notes on what was said are added by time and the summary at the top is refreshed.'
    : 'Live notes on: while recording, notes are added by time every ' + every + '; Stop writes the full summary.', !state.recording);
}

// ---------- Output ----------

async function copyText(text, what) {
  const value = text.trim();
  if (!value) {
    setStatus('No ' + what.toLowerCase() + ' to copy yet.');
    return;
  }
  try {
    await navigator.clipboard.writeText(value);
    setStatus(what + ' copied.');
    return;
  } catch {
    // Older browsers, or the clipboard API is blocked: fall back to a hidden text box.
  }
  const box = document.createElement('textarea');
  box.value = value;
  box.setAttribute('readonly', '');
  box.style.position = 'fixed';
  box.style.top = '0';
  box.style.opacity = '0';
  document.body.appendChild(box);
  box.select();
  let ok = false;
  try { ok = document.execCommand('copy'); } catch {}
  box.remove();
  setStatus(ok ? what + ' copied.' : 'Copying is blocked in this browser. Select the text and copy it yourself.');
}

function downloadMarkdown() {
  if (!el.transcript.value.trim() && !el.notes.value.trim()) {
    setStatus('Nothing to download yet.');
    return;
  }
  const date = new Date(state.startedAt || Date.now());
  const text = buildDocument({
    title: CFG.documentTitle,
    date,
    lengthMs: sessionElapsedMs(),
    transcript: el.transcript.value,
    notes: el.notes.value,
    notesHeading: documentHeading(el.notes.value, state.notesStyle)
  });
  const name = exportFileName(date);
  const url = URL.createObjectURL(new Blob([text], { type: 'text/markdown;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
  setStatus('Downloaded ' + name + '.');
}

async function saveClick() {
  if (!el.transcript.value.trim() && !el.notes.value.trim()) {
    setStatus('Nothing to save yet.');
    return;
  }
  el.savePc.disabled = true;
  try {
    const name = await saveOnPc();
    setStatus('Saved on the PC as ' + name + ' in %APPDATA%\\VoiceChatbotMini\\transcripts.', !state.recording);
  } catch (e) {
    setStatus('Save on PC failed: ' + e.message, true);
  } finally {
    el.savePc.disabled = false;
  }
}

async function sendToChat() {
  const transcript = el.transcript.value.trim(), notes = el.notes.value.trim();
  if (!transcript && !notes) {
    setStatus('Nothing to send yet. Record or paste a transcript first.');
    return;
  }
  el.sendChat.disabled = true;
  try {
    const data = await api(API + '/send-to-chat', { method: 'POST', json: { transcript, notes } });
    setStatus(data.message || 'Sent to the chat on the PC.', !state.recording);
  } catch (e) {
    setStatus('Send to chat failed: ' + e.message, true);
  } finally {
    el.sendChat.disabled = false;
  }
}

function clearSession() {
  if (state.finishing || clearPending) return;
  // Nothing is lost: the words already spoken are transcribed first (the queue runs in order), then the session
  // is saved on the PC and cleared (finishClear). What is said from now on starts the new session at 00:00.
  if (chunker) {
    const rest = chunker.flush();
    if (rest) enqueue(rest);
  }
  clearPending = true;
  updateUi();
  queue.push({
    clear: true,
    shiftMs: chunker ? timelineOriginMs + chunker.positionMs : sessionElapsedMs(), // the click on the chunks' timeline
    elapsedMs: sessionElapsedMs(),
    startedAt: Date.now()
  });
  if (pendingChunks() > 0) setStatus('Clearing once the words already spoken are transcribed...');
  pumpQueue();
}

// Runs from the queue, after the chunks recorded before Clear (mark: the item clearSession queued).
async function finishClear(mark) {
  try {
    let saved = null;
    if (hasUnsaved() && el.transcript.value.trim()) {
      try {
        saved = await saveOnPc(mark.elapsedMs);
      } catch (e) {
        if (!confirm('The transcript could not be saved on the PC (' + e.message + '). Clear it anyway?')) {
          setStatus('Not cleared: the transcript could not be saved on the PC.', !state.recording);
          return;
        }
      }
    }

    state.sessionGen++;
    cancelSummary();
    cancelLiveNotes();
    finalAfterSummary = false;
    policy.reset(Date.now());
    // Chunks recorded after the click belong to the new session, whose clock started at the click.
    for (const item of queue) {
      item.gen = state.sessionGen;
      item.atMs = Math.max(0, item.atMs - mark.shiftMs);
    }
    timelineOriginMs -= mark.shiftMs;
    el.transcript.value = '';
    el.notes.value = '';
    state.notesStyle = CFG.defaultStyle;
    state.notesUpdatedAt = null;
    state.liveNotesFailed = false;
    state.summaryNotRefreshed = false;
    state.notesProblem = '';
    state.saveId = '';
    state.changeVersion = 0;
    state.savedVersion = 0;
    // A new session starts at 00:00 (at the click).
    const elapsed = Math.max(0, sessionElapsedMs() - mark.elapsedMs);
    state.startedAt = state.recording || state.finishing ? mark.startedAt : 0;
    state.elapsedBaseMs = elapsed;
    state.runStart = performance.now();
    updateWords();
    updateNotesHeader();
    removeKey(SESSION_KEY);
    setStatus('Cleared.' + (saved ? ' The previous transcript was saved on the PC as ' + saved + '.' : ''), !state.recording);
  } finally {
    clearPending = false;
    updateUi();
  }
}

// ---------- Resizing the panes ----------

function splitFromPointer(e) {
  const rect = el.panes.getBoundingClientRect();
  const pct = isSideBySide()
    ? (e.clientX - rect.left) / Math.max(1, rect.width) * 100
    : (e.clientY - rect.top) / Math.max(1, rect.height) * 100;
  settings.split = clamp(pct, MIN_SPLIT, MAX_SPLIT);
  applySplit();
}

function endDrag() {
  if (!dragging) return;
  dragging = false;
  el.divider.classList.remove('dragging');
  saveSettings();
}

el.divider.addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  dragging = true;
  el.divider.classList.add('dragging');
  try { el.divider.setPointerCapture(e.pointerId); } catch {}
  e.preventDefault();
});
el.divider.addEventListener('pointermove', e => { if (dragging) splitFromPointer(e); });
el.divider.addEventListener('pointerup', endDrag);
el.divider.addEventListener('pointercancel', endDrag);
el.divider.addEventListener('lostpointercapture', endDrag);
el.divider.addEventListener('dblclick', () => {
  settings.split = DEFAULT_SPLIT;
  applySplit();
  saveSettings();
});
el.divider.addEventListener('keydown', e => {
  const step = e.shiftKey ? 10 : 2;
  let delta = 0;
  if (e.key === 'ArrowUp' || e.key === 'ArrowLeft') delta = -step;
  else if (e.key === 'ArrowDown' || e.key === 'ArrowRight') delta = step;
  if (!delta) return;
  e.preventDefault();
  settings.split = clamp(settings.split + delta, MIN_SPLIT, MAX_SPLIT);
  applySplit();
  saveSettings();
});
window.addEventListener('resize', applySplit);

// ---------- Events ----------

el.startStop.addEventListener('click', () => {
  if (state.recording) stopRecording();
  else startRecording();
});
el.source.addEventListener('change', () => {
  settings.source = el.source.value === 'tab' ? 'tab' : 'mic';
  saveSettings();
  setStatus(settings.source === 'tab'
    ? 'Tab / system audio: after Start, choose a tab (or Entire screen) and turn on "Share tab audio" (or "Share system audio"). Works in Chrome and Edge.'
    : 'Microphone: this device\'s microphone is transcribed. Press Start.', true);
});
el.transcript.addEventListener('input', () => {
  updateWordsSoon();
  markChanged();
});
el.notes.addEventListener('input', () => {
  updateNotesHeader();
  updateNotesControls();
  markChanged();
});
el.style.addEventListener('change', () => {
  settings.style = normalizeStyle(el.style.value);
  saveSettings();
});
el.summarize.addEventListener('click', summarizeClick);
el.liveNotes.addEventListener('click', toggleLiveNotes);
el.interval.addEventListener('change', () => {
  settings.intervalMinutes = normalizeInterval(el.interval.value);
  policy.intervalMs = settings.intervalMinutes * 60000;
  saveSettings();
  if (policy.enabled) setStatus('Live notes are updated every ' + intervalText(settings.intervalMinutes) + ' while recording.');
});
el.copyTranscript.addEventListener('click', () => copyText(el.transcript.value, 'Transcript'));
el.copyNotes.addEventListener('click', () => copyText(el.notes.value, 'Notes'));
el.download.addEventListener('click', downloadMarkdown);
el.savePc.addEventListener('click', saveClick);
el.sendChat.addEventListener('click', sendToChat);
el.clear.addEventListener('click', clearSession);
el.fontDown.addEventListener('click', () => setFontSize(settings.fontSize - 1));
el.fontUp.addEventListener('click', () => setFontSize(settings.fontSize + 1));
el.pinSave.addEventListener('click', submitPin);
el.pin.addEventListener('input', () => el.pin.classList.remove('needed'));
el.pin.addEventListener('keydown', e => {
  if (e.key === 'Enter') {
    e.preventDefault();
    submitPin();
  }
});

document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible') {
    if (state.recording) {
      // The screen lock is released whenever the page is hidden.
      acquireWakeLock();
      if (capture && capture.ctx.state === 'suspended') capture.ctx.resume().catch(() => {});
    }
  } else {
    persistNow();
  }
});
window.addEventListener('pagehide', persistNow);
window.addEventListener('beforeunload', e => {
  persistNow();
  if (state.recording || state.finishing || stopPromise || queue.length > 0 || summaryJob || liveJob) {
    e.preventDefault();
    e.returnValue = '';
  }
});

// ---------- Start ----------

loadSettings();
applySettings();
loadPin();
const restored = restoreSession();
updateUi();
updateWords();
updateNotesHeader();
updateLiveNotesUi();
if (restored) el.transcript.scrollTop = el.transcript.scrollHeight;

if (isPhone) {
  el.notice.textContent = 'On a phone, keep this page open with the screen on while recording: phones stop the microphone when the screen locks or you switch apps.' +
    ('wakeLock' in navigator ? ' The page keeps the screen awake while it records.' : ' This browser cannot keep the screen awake, so turn off auto-lock while recording.');
  el.notice.hidden = false;
}

if (!window.isSecureContext || !navigator.mediaDevices) {
  setStatus('Recording needs the https:// address of the remote. Open the address shown in the desktop app under Settings > Phone Remote.', true);
} else if (restored) {
  setStatus('Restored your last session from ' + restored.toLocaleString([], { dateStyle: 'short', timeStyle: 'short' }) + '. Clear starts a new one.', true);
} else {
  setStatus(canShareTab ? 'Ready. Choose Microphone or Tab / system audio and press Start.' : 'Ready. Press Start to transcribe the microphone.', true);
}

if (pinValue) checkPin(false);
else showPinBar();
setInterval(tick, 200);
</script>
</body>
</html>
""";
}
