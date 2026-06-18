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

    private var api: VoiceChatAPI
    private let audio = BackgroundConversationAudio()
    private var processingSegment = false

    init() {
        let initialProfile: ServerProfile
        if let data = UserDefaults.standard.data(forKey: "serverProfile"),
           let saved = try? JSONDecoder().decode(ServerProfile.self, from: data) {
            initialProfile = saved
        } else {
            initialProfile = ServerProfile()
        }
        profile = initialProfile
        api = VoiceChatAPI(profile: initialProfile)

        audio.onSpeechSegment = { [weak self] wav in
            Task { @MainActor in
                await self?.process(wav: wav)
            }
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
    }

    func applyProfile() async {
        api = VoiceChatAPI(profile: profile)
        await refreshStatus()
    }

    func importCertificate(_ data: Data) {
        profile.pinnedCertificateDER = data
        api = VoiceChatAPI(profile: profile)
    }

    func refreshStatus() async {
        do {
            status = try await api.status()
        } catch {
            fail(error)
        }
    }

    func startSession() async {
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
            conversationState = .listening
        } catch {
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
            fail(error)
            try? audio.startListening()
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
            fail(error)
            if isConversationActive {
                try? audio.startListening()
            }
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
