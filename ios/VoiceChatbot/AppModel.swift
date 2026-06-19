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

    private var api: VoiceChatAPI
    private let audio = BackgroundConversationAudio()
    private let networkMonitor = NetworkChangeMonitor()
    private var processingSegment = false
    private var networkRefreshTask: Task<Void, Never>?

    init() {
        let initialProfile: ServerProfile
        if let data = UserDefaults.standard.data(forKey: "serverProfile"),
           let saved = try? JSONDecoder().decode(ServerProfile.self, from: data) {
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
            guard let self, self.isConversationActive else { return }
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
        isConnecting = true
        defer { isConnecting = false }
        status = nil
        activeRoute = ""
        activeServerURL = ""

        for candidate in profile.endpointCandidates {
            do {
                let probe = VoiceChatAPI(
                    profile: profile,
                    baseURL: candidate.url,
                    probeMode: true
                )
                let discoveredStatus = try await probe.status()
                api = VoiceChatAPI(profile: profile, baseURL: candidate.url)
                status = discoveredStatus
                activeRoute = candidate.name
                activeServerURL = candidate.url
                connectionMessage = "Connected automatically via \(candidate.name)"
                return
            } catch {
                continue
            }
        }

        connectionMessage = profile.endpointCandidates.isEmpty
            ? "Enter at least one server address."
            : "Could not reach the PC on Home Wi-Fi or VPN."
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
        guard let index = messages.firstIndex(where: { $0.id == id }),
              messages[index].role == .assistant
        else { return }

        playingMessageID = id
        defer { playingMessageID = nil }
        do {
            let audioPath: String
            if let existing = messages[index].audioURL, !existing.isEmpty {
                audioPath = existing
            } else {
                audioPath = try await api.speak(messages[index].text)
                messages[index].audioURL = audioPath
            }
            let data = try await api.audioData(relativePath: audioPath)
            try audio.play(data)
        } catch {
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
