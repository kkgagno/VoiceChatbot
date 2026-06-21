import Foundation
import MessageUI
import Observation
import UIKit

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
    var pendingCalendarDeletion: CalendarDeletionDraft?
    var pendingTextMessage: TextMessageDraft?
    var pendingContactDisambiguation: ContactDisambiguationDraft?
    var pendingTextClarificationRequest: String?

    private var api: VoiceChatAPI
    let calendar = CalendarService()
    let contacts = ContactsService()
    let health = HealthService()
    let wakeWord = WakeWordService()
    private let audio = BackgroundConversationAudio()
    private let networkMonitor = NetworkChangeMonitor()
    private var processingSegment = false
    private var networkRefreshTask: Task<Void, Never>?
    private var connectionProbeGeneration = 0
    private var healthDiscussionTurns = [HealthDiscussionTurn]()
    private var healthDiscussionData = ""
    private var healthDiscussionUpdatedAt: Date?
    private var returnToWakeModeAfterResponse = false

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
            if self.returnToWakeModeAfterResponse {
                self.finishWakeRequest()
                return
            }
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
        wakeWord.onDetection = { [weak self] in
            Task { @MainActor in
                await self?.beginWakeRequest()
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
        wakeWord.stop()
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

    func applyWakeWordSettings() {
        wakeWord.saveSettings()
        if wakeWord.isListening {
            wakeWord.stop()
            wakeWord.statusMessage = "Wake mode stopped"
            return
        }
        guard wakeWord.isEnabled else {
            wakeWord.stop()
            return
        }
        audio.stop()
        isConversationActive = false
        conversationState = .idle
        do {
            try wakeWord.start()
        } catch {
            wakeWord.statusMessage = error.localizedDescription
        }
    }

    private func beginWakeRequest() async {
        guard !isConversationActive else { return }
        await refreshStatus()
        guard status?.ok == true, await audio.requestPermission() else {
            wakeWord.statusMessage = "Could not start the voice request"
            try? wakeWord.start()
            return
        }
        do {
            returnToWakeModeAfterResponse = true
            try audio.startListening()
            isConversationActive = true
            conversationState = .listening
            audio.updateNowPlaying(active: true, title: "Wake word detected")
            UINotificationFeedbackGenerator().notificationOccurred(.success)
        } catch {
            returnToWakeModeAfterResponse = false
            wakeWord.statusMessage = error.localizedDescription
            try? wakeWord.start()
        }
    }

    private func finishWakeRequest() {
        returnToWakeModeAfterResponse = false
        audio.stop()
        isConversationActive = false
        conversationState = .idle
        guard wakeWord.isEnabled else { return }
        do {
            try wakeWord.start()
        } catch {
            wakeWord.statusMessage = error.localizedDescription
        }
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
        if pendingAttachments.isEmpty, await handleTextMessageCommand(text) {
            return
        }
        if pendingAttachments.isEmpty, await handleCalendarCommand(text) {
            return
        }
        if pendingAttachments.isEmpty, await handleHealthQuestion(text) {
            return
        }
        clearHealthDiscussion()
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
                if await handleTextMessageCommand(transcript) {
                    return
                }
                if await handleCalendarCommand(transcript) {
                    return
                }
                if await handleHealthQuestion(transcript) {
                    return
                }
                clearHealthDiscussion()
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

    private func send(text: String, displayText: String? = nil) async {
        let attachments = pendingAttachments
        pendingAttachments = []
        let attachmentSummary = attachments.isEmpty
            ? ""
            : "\n\nAttached: " + attachments.map(\.name).joined(separator: ", ")
        messages.append(
            ChatEntry(role: .user, text: (displayText ?? text) + attachmentSummary)
        )
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
                if returnToWakeModeAfterResponse {
                    finishWakeRequest()
                } else {
                    try audio.startListening()
                    conversationState = .listening
                }
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

        await calendar.requestAccessAndLoad()
        guard calendar.hasFullAccess else {
            messages.append(ChatEntry(role: .user, text: text))
            failMessage(calendar.errorMessage ?? "Calendar access is required.")
            return true
        }

        switch command {
        case .create:
            messages.append(ChatEntry(role: .user, text: text))
            conversationState = .thinking
            do {
                let draft = try await createCalendarDraftWithAI(from: text)
                pendingCalendarEvent = draft
                messages.append(
                    ChatEntry(
                        role: .system,
                        text: "The AI prepared this calendar event. Review and confirm it before it is added."
                    )
                )
                if isConversationActive {
                    audio.pause()
                    conversationState = .paused
                } else {
                    conversationState = .idle
                }
            } catch {
                fail(error)
            }
        case .delete:
            messages.append(ChatEntry(role: .user, text: text))
            conversationState = .thinking
            do {
                let draft = try await createCalendarDeletionWithAI(from: text)
                pendingCalendarDeletion = draft
                messages.append(
                    ChatEntry(
                        role: .system,
                        text: "The AI found \(draft.items.count) matching event\(draft.items.count == 1 ? "" : "s"). Review them before deletion."
                    )
                )
                if isConversationActive {
                    audio.pause()
                    conversationState = .paused
                } else {
                    conversationState = .idle
                }
            } catch {
                fail(error)
            }
        case .list:
            calendar.loadUpcoming()
            let groundedPrompt = """
            Answer the user's calendar question using only the live iPhone calendar data below.
            Reason naturally about relative dates and ranges such as today, tomorrow, this weekend,
            or the next two days. Be concise. If the requested period has no matching events, say so.
            Do not claim you cannot access the calendar.

            User's question:
            \(text)

            \(calendar.modelContext())
            """
            do {
                let answer = try await api.tool(prompt: groundedPrompt)
                messages.append(ChatEntry(role: .user, text: text))
                messages.append(ChatEntry(role: .assistant, text: answer))
                if isConversationActive {
                    conversationState = .speaking
                    let audioPath = try await api.speak(answer)
                    let data = try await api.audioData(relativePath: audioPath)
                    try audio.play(data)
                } else {
                    conversationState = .idle
                }
            } catch {
                fail(error)
            }
        }
        return true
    }

    private func handleTextMessageCommand(_ text: String) async -> Bool {
        let originalRequest = pendingTextClarificationRequest
        guard originalRequest != nil
                || TextMessageCommandParser.isTextMessageDirective(text)
        else { return false }
        let effectiveRequest = originalRequest.map {
            "\($0)\n\nUser clarification: \(text)"
        } ?? text

        let intent: TextMessagePreparation
        do {
            do {
                intent = try await api.prepareTextMessage(effectiveRequest)
            } catch {
                // The desktop server may have restarted or the phone may have
                // switched between LAN and Tailscale since the active API
                // session was selected. Re-probe both routes once and retry
                // against the newly selected server.
                await refreshStatus()
                guard status?.ok == true else {
                    throw error
                }
                intent = try await api.prepareTextMessage(effectiveRequest)
            }
            guard intent.isTextMessage else { return false }
        } catch {
            messages.append(ChatEntry(role: .user, text: text))
            failMessage(
                "Text preparation failed at \(activeRoute.isEmpty ? "the PC connection" : activeRoute) "
                    + "— \(error.localizedDescription)"
            )
            return true
        }

        messages.append(ChatEntry(role: .user, text: text))
        if intent.needsClarification {
            pendingTextClarificationRequest = effectiveRequest
            let question = intent.clarificationQuestion
                .trimmingCharacters(in: .whitespacesAndNewlines)
            messages.append(
                ChatEntry(
                    role: .assistant,
                    text: question.isEmpty
                        ? "Which person, title, or subject did you mean?"
                        : question
                )
            )
            conversationState = .idle
            return true
        }
        pendingTextClarificationRequest = nil

        await contacts.requestAccessAndLoad()
        guard contacts.hasAccess else {
            failMessage(contacts.errorMessage ?? "Contacts access is required.")
            return true
        }

        conversationState = .thinking
        do {
            let recipient = intent.recipient.trimmingCharacters(in: .whitespacesAndNewlines)
            let body = intent.body.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !recipient.isEmpty,
                  !body.isEmpty
            else {
                throw ContactsFeatureError.invalidAIDraft
            }
            let choices = contacts.choices(matching: recipient)
            guard !choices.isEmpty else {
                throw ContactsFeatureError.contactNotFound
            }
            if choices.count == 1, let choice = choices.first {
                prepareTextMessage(choice: choice, body: body)
            } else {
                pendingContactDisambiguation = ContactDisambiguationDraft(
                    originalRequest: text,
                    messageBody: body,
                    choices: choices
                )
                messages.append(
                    ChatEntry(
                        role: .system,
                        text: "I found \(choices.count) possible recipients. Choose who you meant."
                    )
                )
            }
            if isConversationActive {
                audio.pause()
                conversationState = .paused
            } else {
                conversationState = .idle
            }
        } catch {
            failMessage(error.localizedDescription)
        }
        return true
    }

    private func handleHealthQuestion(_ text: String) async -> Bool {
        let isExplicitHealthQuestion = HealthQuestionParser.isHealthQuestion(text)
        let discussionIsRecent = healthDiscussionUpdatedAt.map {
            Date.now.timeIntervalSince($0) < 20 * 60
        } ?? false
        let isFollowUp = discussionIsRecent
            && !healthDiscussionTurns.isEmpty
            && HealthQuestionParser.isLikelyFollowUp(text)
        guard isExplicitHealthQuestion || isFollowUp else { return false }

        messages.append(ChatEntry(role: .user, text: text))
        conversationState = .thinking
        do {
            let shouldRefreshData = healthDiscussionData.isEmpty
                || isExplicitHealthQuestion
                || (healthDiscussionUpdatedAt.map {
                    Date.now.timeIntervalSince($0) > 5 * 60
                } ?? true)
            if shouldRefreshData {
                healthDiscussionData = try await health.modelContext()
            }
            let priorDiscussion = healthDiscussionTurns.suffix(8).map {
                "\($0.role): \($0.text)"
            }.joined(separator: "\n")
            let prompt = """
            Have a natural, clinically informed health conversation with the user using only the
            live Apple Health data below. Sound warm and conversational, like a thoughtful health
            professional explaining the important takeaway aloud—not like a report or spreadsheet.

            Answer the exact question first. By default use 2–4 spoken sentences, mention only the
            one to three numbers that materially support the answer, and summarize patterns instead
            of reading sample lists. Do not use bullets unless the user asks for a breakdown.
            Clearly distinguish missing data from zero. Do not diagnose, prescribe treatment, or
            pretend to be the user's doctor. Use a medical caution only when the data or question
            genuinely warrants it; do not repeat generic disclaimers in every answer.

            Recent private health discussion on this iPhone:
            \(priorDiscussion.isEmpty ? "No prior health discussion." : priorDiscussion)

            User question:
            \(text)

            \(healthDiscussionData)
            """
            let answer: String
            do {
                answer = try await api.tool(prompt: prompt)
            } catch {
                await refreshStatus()
                guard status?.ok == true else { throw error }
                answer = try await api.tool(prompt: prompt)
            }
            healthDiscussionTurns.append(
                HealthDiscussionTurn(role: "User", text: text)
            )
            healthDiscussionTurns.append(
                HealthDiscussionTurn(role: "Assistant", text: answer)
            )
            healthDiscussionTurns = Array(healthDiscussionTurns.suffix(10))
            healthDiscussionUpdatedAt = .now
            messages.append(ChatEntry(role: .assistant, text: answer))
            if isConversationActive {
                conversationState = .speaking
                let audioPath = try await api.speak(answer)
                let data = try await api.audioData(relativePath: audioPath)
                try audio.play(data)
            } else {
                conversationState = .idle
            }
        } catch {
            failMessage(error.localizedDescription)
        }
        return true
    }

    private func clearHealthDiscussion() {
        healthDiscussionTurns = []
        healthDiscussionData = ""
        healthDiscussionUpdatedAt = nil
    }

    func selectTextRecipient(
        _ choice: ContactChoice,
        from draft: ContactDisambiguationDraft
    ) {
        pendingContactDisambiguation = nil
        prepareTextMessage(choice: choice, body: draft.messageBody)
    }

    func cancelContactDisambiguation() {
        pendingContactDisambiguation = nil
        messages.append(ChatEntry(role: .system, text: "Text recipient selection canceled."))
        resumeAfterCalendarSheet()
    }

    private func prepareTextMessage(choice: ContactChoice, body: String) {
        pendingTextMessage = TextMessageDraft(
            contactID: choice.contact.id,
            contactName: choice.contact.name,
            phoneNumber: choice.phoneNumber,
            body: body
        )
        messages.append(
            ChatEntry(
                role: .system,
                text: "The AI prepared a text to \(choice.contact.name). Review it before opening Messages."
            )
        )
    }

    func completeTextMessage(result: MessageComposeResult, draft: TextMessageDraft) {
        switch result {
        case .sent:
            pendingTextMessage = nil
            messages.append(
                ChatEntry(role: .assistant, text: "Text sent to \(draft.contactName).")
            )
            resumeAfterCalendarSheet()
        case .failed:
            failMessage("The text message could not be sent.")
        case .cancelled:
            messages.append(ChatEntry(role: .system, text: "Text was not sent."))
        @unknown default:
            messages.append(ChatEntry(role: .system, text: "Messages closed without sending."))
        }
    }

    func cancelTextMessage() {
        pendingTextMessage = nil
        messages.append(ChatEntry(role: .system, text: "Text message canceled."))
        resumeAfterCalendarSheet()
    }

    private func createCalendarDraftWithAI(from request: String) async throws -> CalendarEventDraft {
        let now = Date.now.formatted(date: .complete, time: .complete)
        let timeZone = TimeZone.current.identifier
        let prompt = """
        You are an expert calendar assistant. Convert the user's request into one calendar event.
        Resolve relative dates using the supplied current local date, time, and time zone.
        If the year is omitted, choose the next future occurrence. Infer a concise useful title.
        Infer a sensible duration: use one hour when no duration or ending time is stated.
        Put useful extra details in notes, but do not invent people, locations, or facts.

        Return ONLY one JSON object with exactly these string fields:
        {"title":"...","start":"ISO-8601 with UTC offset","end":"ISO-8601 with UTC offset","notes":"..."}

        Current local date and time: \(now)
        Time zone: \(timeZone)
        User request: \(request)
        """
        let result = try await api.tool(prompt: prompt)
        return try CalendarAIDraft.decode(from: result).eventDraft()
    }

    private func createCalendarDeletionWithAI(
        from request: String
    ) async throws -> CalendarDeletionDraft {
        calendar.loadUpcoming()
        let prompt = """
        You are an expert calendar assistant. Select only the live calendar events that clearly
        match the user's deletion request. Resolve relative dates using the supplied local date,
        time, and time zone. Never guess an event ID. If nothing matches, return an empty eventIDs array.

        Return ONLY one JSON object:
        {"eventIDs":["exact event id"],"futureSeriesEventIDs":["exact recurring event id"]}

        Use futureSeriesEventIDs only when the user explicitly asks to delete an entire recurring
        series or this and all future occurrences. Otherwise leave it empty.

        User request:
        \(request)

        \(calendar.modelContext())
        """
        let result = try await api.tool(prompt: prompt)
        let selection = try CalendarAIDeleteSelection.decode(from: result)
        let selectedIDs = Set(selection.eventIDs)
        let futureIDs = Set(selection.futureSeriesEventIDs ?? [])
        let items = calendar.events
            .filter { selectedIDs.contains($0.id) }
            .map {
                CalendarDeletionItem(
                    event: $0,
                    deleteFutureEvents: $0.isRecurring && futureIDs.contains($0.id)
                )
            }
        guard !items.isEmpty else {
            throw CalendarFeatureError.noMatchingEvents
        }
        return CalendarDeletionDraft(items: items)
    }

    func saveCalendarEvent(_ draft: CalendarEventDraft) -> Bool {
        do {
            let saved = try calendar.save(draft)
            pendingCalendarEvent = nil
            let when = saved.startDate.formatted(date: .complete, time: .shortened)
            messages.append(
                ChatEntry(
                    role: .assistant,
                    text: "Verified: \(saved.title) was added to “\(saved.calendarTitle)” for \(when)."
                )
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

    func deleteCalendarEvents(_ draft: CalendarDeletionDraft) -> Bool {
        do {
            let deleted = try calendar.delete(draft)
            pendingCalendarDeletion = nil
            messages.append(
                ChatEntry(
                    role: .assistant,
                    text: "Deleted \(deleted) calendar event\(deleted == 1 ? "" : "s")."
                )
            )
            resumeAfterCalendarSheet()
            return true
        } catch {
            fail(error)
            return false
        }
    }

    func cancelCalendarDeletion() {
        pendingCalendarDeletion = nil
        messages.append(ChatEntry(role: .system, text: "Calendar deletion canceled."))
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
