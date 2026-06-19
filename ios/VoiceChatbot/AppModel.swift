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
            do {
                try self.audio.startListening()
                self.conversationState = .listening
            } catch {
                self.fail(error)
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

    func sendTypedMessage() async {
        let text = typedMessage.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        typedMessage = ""
        await send(text: text)
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
        messages.append(ChatEntry(role: .user, text: text))
        do {
            conversationState = .thinking
            let result = try await api.respond(to: text)
            messages.append(ChatEntry(role: .assistant, text: result.response))
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
