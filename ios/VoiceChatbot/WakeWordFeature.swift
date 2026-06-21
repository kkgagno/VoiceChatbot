import AVFAudio
import Foundation
import Observation
import Speech
import SwiftUI

@MainActor
@Observable
final class WakeWordService {
    var isEnabled = UserDefaults.standard.bool(forKey: "wakeWordEnabled")
    var phrase = UserDefaults.standard.string(forKey: "wakeWordPhrase") ?? "Hey Computer"
    var isListening = false
    var statusMessage = "Wake word is off"
    var lastHeardText = ""

    var onDetection: (() -> Void)?

    private let engine = AVAudioEngine()
    private var recognitionRequest: SFSpeechAudioBufferRecognitionRequest?
    private var recognitionTask: SFSpeechRecognitionTask?
    private var restartTask: Task<Void, Never>?
    private var tapInstalled = false
    private var speechRecognizer = SFSpeechRecognizer(locale: .current)

    func saveSettings() {
        phrase = phrase.trimmingCharacters(in: .whitespacesAndNewlines)
        UserDefaults.standard.set(isEnabled, forKey: "wakeWordEnabled")
        UserDefaults.standard.set(phrase, forKey: "wakeWordPhrase")
    }

    func start() async throws {
        stop(preserveStatus: true)
        let configuredPhrase = normalized(phrase)
        guard !configuredPhrase.isEmpty else {
            throw WakeWordError.phraseRequired
        }
        guard await requestSpeechPermission() else {
            throw WakeWordError.speechPermissionRequired
        }
        guard await AVAudioApplication.requestRecordPermission() else {
            throw WakeWordError.microphonePermissionRequired
        }
        guard let recognizer = speechRecognizer, recognizer.isAvailable else {
            throw WakeWordError.recognizerUnavailable
        }
        guard recognizer.supportsOnDeviceRecognition else {
            throw WakeWordError.onDeviceRecognitionUnavailable
        }

        let session = AVAudioSession.sharedInstance()
        try session.setCategory(
            .playAndRecord,
            mode: .measurement,
            options: [.defaultToSpeaker, .allowBluetooth]
        )
        try session.setActive(true)

        let request = SFSpeechAudioBufferRecognitionRequest()
        request.shouldReportPartialResults = true
        request.requiresOnDeviceRecognition = true
        request.addsPunctuation = false
        request.taskHint = .confirmation
        request.contextualStrings = [phrase]
        recognitionRequest = request

        let input = engine.inputNode
        input.installTap(onBus: 0, bufferSize: 1_024, format: nil) {
            [weak request] buffer, _ in
            request?.append(buffer)
        }
        tapInstalled = true

        recognitionTask = recognizer.recognitionTask(with: request) {
            [weak self] result, error in
            Task { @MainActor in
                guard let self, self.isListening else { return }
                if let result {
                    let heard = result.bestTranscription.formattedString
                    self.lastHeardText = heard
                    if self.containsWakePhrase(heard) {
                        self.statusMessage = "Wake phrase detected"
                        self.stop(preserveStatus: true)
                        self.onDetection?()
                        return
                    }
                }
                if error != nil || result?.isFinal == true {
                    self.scheduleRestart()
                }
            }
        }

        engine.prepare()
        do {
            try engine.start()
        } catch {
            stop(preserveStatus: true)
            throw error
        }
        isListening = true
        statusMessage = "Listening locally for “\(phrase)”"
    }

    func stop(preserveStatus: Bool = false) {
        restartTask?.cancel()
        restartTask = nil
        recognitionRequest?.endAudio()
        recognitionTask?.cancel()
        recognitionRequest = nil
        recognitionTask = nil
        if tapInstalled {
            engine.inputNode.removeTap(onBus: 0)
            tapInstalled = false
        }
        if engine.isRunning {
            engine.stop()
        }
        engine.reset()
        isListening = false
        lastHeardText = ""
        if !preserveStatus {
            statusMessage = isEnabled ? "Wake mode stopped" : "Wake word is off"
        }
    }

    private func scheduleRestart() {
        guard isEnabled, isListening, restartTask == nil else { return }
        stop(preserveStatus: true)
        restartTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(300))
            guard !Task.isCancelled, let self, self.isEnabled else { return }
            self.restartTask = nil
            do {
                try await self.start()
            } catch {
                self.statusMessage = error.localizedDescription
            }
        }
    }

    private func containsWakePhrase(_ text: String) -> Bool {
        let heard = normalized(text)
        let target = normalized(phrase)
        guard !heard.isEmpty, !target.isEmpty else { return false }
        return (" " + heard + " ").contains(" " + target + " ")
    }

    private func normalized(_ value: String) -> String {
        value
            .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
            .joined(separator: " ")
            .lowercased()
    }

    private func requestSpeechPermission() async -> Bool {
        switch SFSpeechRecognizer.authorizationStatus() {
        case .authorized:
            return true
        case .notDetermined:
            return await withCheckedContinuation { continuation in
                SFSpeechRecognizer.requestAuthorization {
                    continuation.resume(returning: $0 == .authorized)
                }
            }
        default:
            return false
        }
    }
}

enum WakeWordError: LocalizedError {
    case phraseRequired
    case speechPermissionRequired
    case microphonePermissionRequired
    case recognizerUnavailable
    case onDeviceRecognitionUnavailable

    var errorDescription: String? {
        switch self {
        case .phraseRequired:
            "Enter a wake phrase."
        case .speechPermissionRequired:
            "Allow Speech Recognition in iPhone Settings."
        case .microphonePermissionRequired:
            "Microphone permission is required."
        case .recognizerUnavailable:
            "Apple’s speech recognizer is currently unavailable."
        case .onDeviceRecognitionUnavailable:
            "On-device speech recognition is unavailable for the current iPhone language."
        }
    }
}

struct WakeWordView: View {
    @Bindable var model: AppModel
    @Bindable var wakeWord: WakeWordService

    init(model: AppModel) {
        self.model = model
        self.wakeWord = model.wakeWord
    }

    var body: some View {
        Form {
            Section("Local wake phrase") {
                Toggle("Enable wake phrase", isOn: $wakeWord.isEnabled)
                TextField("Wake phrase", text: $wakeWord.phrase)
                    .textInputAutocapitalization(.words)
                    .autocorrectionDisabled()
                Text("Examples: “Hey Computer”, “Lagertha”, or another distinctive phrase.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                Button(
                    wakeWord.isListening
                        ? "Stop Wake Mode"
                        : "Save and Start Wake Mode"
                ) {
                    model.applyWakeWordSettings()
                }
                .buttonStyle(.borderedProminent)
                Text(wakeWord.statusMessage)
                    .foregroundStyle(.secondary)
            }

            Section("How it works") {
                Text("Apple’s on-device speech recognizer listens locally for the configured phrase. Audio is not sent to the PC until the phrase is detected.")
                Text("After detection, pause briefly and speak one request. The app answers, then returns to wake mode.")
                Text("Force-quitting the app stops wake mode. Continuous microphone use increases battery consumption.")
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
        .navigationTitle("Wake Word")
        .navigationBarTitleDisplayMode(.inline)
    }
}
