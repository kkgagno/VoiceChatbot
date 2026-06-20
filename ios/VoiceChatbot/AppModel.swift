import Foundation
import Observation

@MainActor
@Observable
final class AppModel {
    var profile: ServerProfile {
        didSet { saveProfile() }
    }
    var status: ServerStatus?
    var conversationState: ConversationState = .idle
    var messages = [ChatEntry]()
    var typedMessage = ""
    var isConversationActive = false
    var activeMode: ActiveSessionMode = .conversation
    var transcript = ""
    var isConnecting = false
    var connectionMessage = "Not connected"
    var activeRoute = ""
    var activeServerURL = ""
    var pendingAttachments = [PendingAttachment]()
    var keepDocumentsActive = false
    var activeDocumentCount = 0
    var playingMessageID: UUID?
    var comfyOutput: ComfyOutput?
    var isRunningComfy = false
    var pendingCalendarEvent: CalendarEventDraft?

    private var api: VoiceChatAPI
    let calendar = CalendarService()
    private let audio = BackgroundConversationAudio()
    private let networkMonitor = NetworkChangeMonitor()
    private var processingSegment = false
    private var networkRefreshTask: Task<Void, Never>?
    private var connectionProbeGeneration = 0

    init() {
        let initialProfile: ServerProfile
        if let data = UserDefaults.standard.data(forKey: "serverProfile"),
           var saved = try? JSONDecoder().decode(ServerProfile.self, from: data) {
            if saved.vpnURL == "https://100.94.67.49:5100"
                || saved.vpnURL == "https://10.8.0.1:5100" {
                saved.vpnURL = "http://minilagertha.tail2762b8.ts.net:5101"
            }
            initialProfile = saved
        } else {
            initialProfile = ServerProfile()
        }
        profile = initialProfile
        api = VoiceChatAPI(
            profile: initialProfile,
            baseURL: initialProfile.endpointCandidates.first?.url ?? ""
        )

        audio.onSpeechSegment = { [weak self] wav in
            Task { @MainActor in
                await self?.process(wav: wav)
            }
        }
        audio.onSpeechCandidate = { [weak self] wav in
            guard let self else { return false }
            return (try? await self.api.detectSpeech(wav: wav)) ?? true
        }
        audio.onPlaybackFinished = { [weak self] in
            guard let self else { return }
            self.playingMessageID = nil
            guard self.isConversationActive else { return }
            Task { @MainActor in
                await self.restartListeningAfterPlayback()
            }
        }
        audio.onRemotePause = { [weak self] in
            self?.conversationState = .paused
        }
        audio.onRemoteResume = { [weak self] in
            guard let self else { return }
            self.isConversationActive = true
            self.conversationState = .listening
        }
        audio.onRemoteStop = { [weak self] in
            self?.isConversationActive = false
            self?.conversationState = .idle
        }

        networkMonitor.start { [weak self] in
            Task { @MainActor in
                self?.scheduleNetworkRefresh()
            }
        }
    }

    func applyProfile() async {
        await refreshStatus()
    }

    func importCertificate(_ data: Data) {
        profile.pinnedCertificateDER = data
        activeRoute = ""
        activeServerURL = ""
    }

    func refreshStatus() async {
        connectionProbeGeneration += 1
        let generation = connectionProbeGeneration
        isConnecting = true
        status = nil
        activeRoute = ""
        activeServerURL = ""

        let currentProfile = profile
        let candidates = currentProfile.endpointCandidates
        let probeResult = await withTaskGroup(of: ConnectionProbeResult.self) { group in
            for candidate in candidates {
                group.addTask {
                    do {
                        if candidate.name == "VPN" {
                            try? await Task.sleep(for: .milliseconds(200))
                        }
                        let probe = VoiceChatAPI(
                            profile: currentProfile,
                            baseURL: candidate.url,
                            probeMode: true
                        )
                        let discoveredStatus = try await probe.status()
                        return ConnectionProbeResult(
                            attempt: ConnectionAttempt(
                                name: candidate.name,
                                url: candidate.url,
                                status: discoveredStatus
                            ),
                            failure: nil
                        )
                    } catch {
                        let nsError = error as NSError
                        return ConnectionProbeResult(
                            attempt: nil,
                            failure: "\(candidate.name) \(candidate.url): \(nsError.domain) \(nsError.code) — \(nsError.localizedDescription)"
                        )
                    }
                }
            }

            var failures = [String]()
            for await result in group {
                if let attempt = result.attempt {
                    group.cancelAll()
                    return (winner: Optional(attempt), failures: failures)
                }
                if let failure = result.failure {
                    failures.append(failure)
                }
            }
            return (winner: Optional<ConnectionAttempt>.none, failures: failures)
        }

        guard generation == connectionProbeGeneration else { return }
        isConnecting = false

        if let winner = probeResult.winner {
            api = VoiceChatAPI(profile: currentProfile, baseURL: winner.url)
            status = winner.status
            activeRoute = winner.name
            activeServerURL = winner.url
            connectionMessage = "Connected automatically via \(winner.name)"
            return
        }

        if candidates.isEmpty {
            connectionMessage = "Enter at least one server address."
        } else {
            connectionMessage = probeResult.failures.isEmpty
                ? "Could not reach the PC on Home Wi-Fi or VPN."
                : probeResult.failures.joined(separator: "\n")
        }
    }

    private struct ConnectionAttempt: Sendable {
        let name: String
        let url: String
        let status: ServerStatus
    }

    private struct ConnectionProbeResult: Sendable {
        let attempt: ConnectionAttempt?
        let failure: String?
    }

    private func scheduleNetworkRefresh() {
        networkRefreshTask?.cancel()
        networkRefreshTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(1.25))
            guard !Task.isCancelled, let self else { return }
            await self.refreshStatus()
        }
    }

    func startSession() async {
        guard !isConversationActive else { return }
        await refreshStatus()
        guard status?.ok == true else {
            failMessage("Connect to your PC in the Connection tab before starting.")
            return
        }
        guard await audio.requestPermission() else {
            failMessage("Microphone permission is required.")
            return
        }
        do {
            try audio.startListening()
            isConversationActive = true
            conversationState = .listening
            audio.updateNowPlaying(
                active: true,
                title: activeMode == .conversation ? "Active conversation" : "Active transcription"
            )
        } catch {
            audio.stop()
            isConversationActive = false
            fail(error)
        }
    }

    func pauseConversation() {
        audio.pause()
        conversationState = .paused
    }

    func resumeConversation() {
        do {
            try audio.resume()
            isConversationActive = true
            conversationState = .listening
        } catch {
            audio.stop()
            isConversationActive = false
            fail(error)
        }
    }

    func stopConversation() {
        audio.stop()
        isConversationActive = false
        conversationState = .idle
    }

    private func restartListeningAfterPlayback() async {
        // iPad needs time for the playback audio unit and route to fully release.
        // Retrying also covers slower Bluetooth and external speaker transitions.
        try? await Task.sleep(for: .milliseconds(400))
        var lastError: Error?
        for attempt in 0..<3 {
            guard isConversationActive else { return }
            do {
                try audio.startListening()
                conversationState = .listening
                return
            } catch {
                lastError = error
                if attempt < 2 {
                    try? await Task.sleep(for: .milliseconds(400))
                }
            }
        }

        audio.stop()
        isConversationActive = false
        fail(lastError ?? AudioRestartError.failed)
    }

    func sendTypedMessage() async {
        let text = typedMessage.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty || !pendingAttachments.isEmpty else { return }
        typedMessage = ""
        if pendingAttachments.isEmpty, await handleCalendarCommand(text) {
            return
        }
        await send(text: text.isEmpty ? "Review the attached file." : text)
    }

    func addAttachment(
        name: String,
        mimeType: String,
        data: Data,
        kind: PendingAttachment.Kind
    ) {
        let maximumFileBytes = 25 * 1024 * 1024
        let maximumTotalBytes = 50 * 1024 * 1024
        guard pendingAttachments.count < 8 else {
            failMessage("You can attach up to 8 files at once.")
            return
        }
        guard data.count <= maximumFileBytes else {
            failMessage("\(name) is larger than the 25 MB attachment limit.")
            return
        }
        guard pendingAttachments.reduce(0, { $0 + $1.data.count }) + data.count <= maximumTotalBytes else {
            failMessage("Attachments may total up to 50 MB per message.")
            return
        }
        pendingAttachments.append(
            PendingAttachment(name: name, mimeType: mimeType, data: data, kind: kind)
        )
    }

    func removeAttachment(id: UUID) {
        pendingAttachments.removeAll { $0.id == id }
    }

    func playResponse(id: UUID) async {
        if playingMessageID == id {
            audio.stopPlayback()
            playingMessageID = nil
            return
        }

        guard let index = messages.firstIndex(where: { $0.id == id }),
              messages[index].role == .assistant
        else { return }

        if playingMessageID != nil {
            audio.stopPlayback(notifyFinished: false)
        }
        playingMessageID = id
        do {
            let audioPath: String
            if let existing = messages[index].audioURL, !existing.isEmpty {
                audioPath = existing
            } else {
                audioPath = try await api.speak(messages[index].text)
                messages[index].audioURL = audioPath
            }
            let data = try await api.audioData(relativePath: audioPath)
            guard playingMessageID == id else { return }
            try audio.play(data)
        } catch {
            playingMessageID = nil
            fail(error)
        }
    }

    func runModelCommand(_ command: String) async {
        await send(text: command)
        await refreshStatus()
    }

    func runComfy(
        action: ComfyAction,
        prompt: String,
        attachments: [PendingAttachment],
        seconds: Int
    ) async {
        guard !prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            failMessage("Enter a ComfyUI prompt first.")
            return
        }

        isRunningComfy = true
        comfyOutput = nil
        defer { isRunningComfy = false }

        do {
            let command = action.command(prompt: prompt, seconds: seconds)
            let result = try await api.sendMessage(
                text: command,
                attachments: attachments,
                keepDocumentsActive: false
            )

            var imageData: Data?
            var videoURL: URL?
            if let path = result.imageURL, !path.isEmpty {
                imageData = try await api.mediaData(relativePath: path)
            }
            if let path = result.videoURL, !path.isEmpty {
                let data = try await api.mediaData(relativePath: path)
                let extensionName = path.lowercased().contains(".webm") ? "webm" : "mp4"
                let url = FileManager.default.temporaryDirectory
                    .appending(path: "VoiceChatbot-\(UUID().uuidString).\(extensionName)")
                try data.write(to: url, options: .atomic)
                videoURL = url
            }

            comfyOutput = ComfyOutput(
                message: result.response,
                imageData: imageData,
                videoURL: videoURL
            )
        } catch {
            fail(error)
        }
    }

    private func process(wav: Data) async {
        guard isConversationActive, !processingSegment else { return }
        processingSegment = true
        defer { processingSegment = false }

        do {
            conversationState = .transcribing
            let transcript = try await api.transcribe(wav: wav)
            guard !transcript.isEmpty else {
                try audio.startListening()
                conversationState = .listening
                return
            }
            if activeMode == .transcription {
                appendTranscript(transcript)
                try audio.startListening()
                audio.updateNowPlaying(active: true, title: "Active transcription")
                conversationState = .listening
            } else {
                if await handleCalendarCommand(transcript) {
                    return
                }
                await send(text: transcript)
            }
        } catch {
            audio.stop()
            isConversationActive = false
            fail(error)
        }
    }

    func clearTranscript() {
        transcript = ""
    }

    private func appendTranscript(_ text: String) {
        let timestamp = Date.now.formatted(date: .omitted, time: .shortened)
        let line = "[\(timestamp)] \(text)"
        transcript = transcript.isEmpty ? line : transcript + "\n\n" + line
    }

    private func send(text: String) async {
        let attachments = pendingAttachments
        pendingAttachments = []
        let attachmentSummary = attachments.isEmpty
            ? ""
            : "\n\nAttached: " + attachments.map(\.name).joined(separator: ", ")
        messages.append(ChatEntry(role: .user, text: text + attachmentSummary))
        do {
            conversationState = .thinking
            let result: AssistantResponse
            if attachments.isEmpty {
                result = try await api.respond(
                    to: text,
                    keepDocumentsActive: keepDocumentsActive
                )
            } else {
                result = try await api.sendMessage(
                    text: text,
                    attachments: attachments,
                    keepDocumentsActive: keepDocumentsActive
                )
            }
            activeDocumentCount = result.activeDocumentCount
            messages.append(
                ChatEntry(role: .assistant, text: result.response, audioURL: result.audioURL)
            )
            if isConversationActive, let audioPath = result.audioURL, !audioPath.isEmpty {
                conversationState = .speaking
                let data = try await api.audioData(relativePath: audioPath)
                try audio.play(data)
            } else if isConversationActive {
                try audio.startListening()
                conversationState = .listening
            } else {
                conversationState = .idle
            }
        } catch {
            pendingAttachments.insert(contentsOf: attachments, at: 0)
            audio.stop()
            isConversationActive = false
            fail(error)
        }
    }

    private func handleCalendarCommand(_ text: String) async -> Bool {
        guard let command = CalendarCommandParser.parse(text) else { return false }
        messages.append(ChatEntry(role: .user, text: text))

        await calendar.requestAccessAndLoad()
        guard calendar.hasFullAccess else {
            failMessage(calendar.errorMessage ?? "Calendar access is required.")
            return true
        }

        switch command {
        case .create(let draft):
            pendingCalendarEvent = draft
            messages.append(
                ChatEntry(
                    role: .system,
                    text: "Review and confirm the calendar event before it is added."
                )
            )
            if isConversationActive {
                audio.pause()
                conversationState = .paused
            }
        case .list:
            calendar.loadUpcoming()
            let summary = calendar.spokenSummary()
            messages.append(ChatEntry(role: .assistant, text: summary))
            if isConversationActive {
                do {
                    conversationState = .speaking
                    let audioPath = try await api.speak(summary)
                    let data = try await api.audioData(relativePath: audioPath)
                    try audio.play(data)
                } catch {
                    fail(error)
                }
            } else {
                conversationState = .idle
            }
        }
        return true
    }

    func saveCalendarEvent(_ draft: CalendarEventDraft) -> Bool {
        do {
            try calendar.save(draft)
            pendingCalendarEvent = nil
            let when = draft.startDate.formatted(date: .abbreviated, time: .shortened)
            messages.append(
                ChatEntry(role: .assistant, text: "Added \(draft.title) to your calendar for \(when).")
            )
            resumeAfterCalendarSheet()
            return true
        } catch {
            calendar.errorMessage = error.localizedDescription
            fail(error)
            return false
        }
    }

    func cancelCalendarEvent() {
        pendingCalendarEvent = nil
        messages.append(ChatEntry(role: .system, text: "Calendar event canceled."))
        resumeAfterCalendarSheet()
    }

    private func resumeAfterCalendarSheet() {
        guard isConversationActive else {
            conversationState = .idle
            return
        }
        do {
            try audio.resume()
            conversationState = .listening
        } catch {
            audio.stop()
            isConversationActive = false
            fail(error)
        }
    }

    private func saveProfile() {
        guard let data = try? JSONEncoder().encode(profile) else { return }
        UserDefaults.standard.set(data, forKey: "serverProfile")
    }

    private func fail(_ error: Error) {
        failMessage(error.localizedDescription)
    }

    private func failMessage(_ message: String) {
        conversationState = .failed(message)
        messages.append(ChatEntry(role: .system, text: message))
    }
}
