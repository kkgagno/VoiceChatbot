import Foundation
import Observation
import Porcupine
import Security
import SwiftUI
import UniformTypeIdentifiers

enum WakeKeyword: String, CaseIterable, Identifiable {
    case computer = "Computer"
    case jarvis = "Jarvis"
    case picovoice = "Picovoice"
    case porcupine = "Porcupine"
    case terminator = "Terminator"
    case blueberry = "Blueberry"
    case custom = "Custom .ppn"

    var id: String { rawValue }

    var builtIn: Porcupine.BuiltInKeyword? {
        switch self {
        case .computer: .computer
        case .jarvis: .jarvis
        case .picovoice: .picovoice
        case .porcupine: .porcupine
        case .terminator: .terminator
        case .blueberry: .blueberry
        case .custom: nil
        }
    }
}

@MainActor
@Observable
final class WakeWordService {
    var isEnabled = UserDefaults.standard.bool(forKey: "wakeWordEnabled")
    var keyword = WakeKeyword(
        rawValue: UserDefaults.standard.string(forKey: "wakeWordKeyword") ?? ""
    ) ?? .computer
    var sensitivity = UserDefaults.standard.object(forKey: "wakeWordSensitivity") as? Double ?? 0.55
    var isListening = false
    var statusMessage = "Wake word is off"
    var customKeywordName = UserDefaults.standard.string(forKey: "wakeWordCustomName") ?? ""

    var onDetection: (() -> Void)?

    private var manager: PorcupineManager?
    private let accessKeyAccount = "PicovoiceAccessKey"

    var accessKey: String {
        get { KeychainValue.read(account: accessKeyAccount) ?? "" }
        set { KeychainValue.write(newValue, account: accessKeyAccount) }
    }

    func saveSettings() {
        UserDefaults.standard.set(isEnabled, forKey: "wakeWordEnabled")
        UserDefaults.standard.set(keyword.rawValue, forKey: "wakeWordKeyword")
        UserDefaults.standard.set(sensitivity, forKey: "wakeWordSensitivity")
        UserDefaults.standard.set(customKeywordName, forKey: "wakeWordCustomName")
    }

    func start() throws {
        stop()
        let key = accessKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else {
            throw WakeWordError.accessKeyRequired
        }

        let detected: (Int32) -> Void = { [weak self] _ in
            Task { @MainActor in
                guard let self, self.isListening else { return }
                self.stop()
                self.statusMessage = "Wake word detected"
                self.onDetection?()
            }
        }
        let failed: (Error) -> Void = { [weak self] error in
            Task { @MainActor in
                self?.statusMessage = error.localizedDescription
                self?.isListening = false
            }
        }

        if keyword == .custom {
            guard let path = customKeywordPath else {
                throw WakeWordError.customKeywordRequired
            }
            manager = try PorcupineManager(
                accessKey: key,
                keywordPath: path,
                sensitivity: Float32(sensitivity),
                onDetection: detected,
                errorCallback: failed
            )
        } else if let builtIn = keyword.builtIn {
            manager = try PorcupineManager(
                accessKey: key,
                keyword: builtIn,
                sensitivity: Float32(sensitivity),
                onDetection: detected,
                errorCallback: failed
            )
        }

        try manager?.start()
        isListening = true
        statusMessage = "Listening for “\(displayName)”"
    }

    func stop() {
        try? manager?.stop()
        try? manager?.delete()
        manager = nil
        isListening = false
        if !isEnabled {
            statusMessage = "Wake word is off"
        }
    }

    func importCustomKeyword(from source: URL) throws {
        let accessing = source.startAccessingSecurityScopedResource()
        defer { if accessing { source.stopAccessingSecurityScopedResource() } }
        let directory = try FileManager.default.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: true
        ).appending(path: "WakeWords", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true
        )
        let destination = directory.appending(path: "custom_ios.ppn")
        if FileManager.default.fileExists(atPath: destination.path) {
            try FileManager.default.removeItem(at: destination)
        }
        try FileManager.default.copyItem(at: source, to: destination)
        customKeywordName = source.deletingPathExtension().lastPathComponent
        keyword = .custom
        saveSettings()
    }

    var displayName: String {
        keyword == .custom && !customKeywordName.isEmpty
            ? customKeywordName
            : keyword.rawValue
    }

    private var customKeywordPath: String? {
        guard let directory = try? FileManager.default.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: false
        ) else { return nil }
        let path = directory.appending(path: "WakeWords/custom_ios.ppn").path
        return FileManager.default.fileExists(atPath: path) ? path : nil
    }
}

enum WakeWordError: LocalizedError {
    case accessKeyRequired
    case customKeywordRequired

    var errorDescription: String? {
        switch self {
        case .accessKeyRequired:
            "Enter your Picovoice AccessKey first."
        case .customKeywordRequired:
            "Import an iOS .ppn custom keyword first."
        }
    }
}

private enum KeychainValue {
    static let service = "com.keithgagnon.VoiceChatbot"

    static func read(account: String) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data
        else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func write(_ value: String, account: String) {
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        SecItemDelete(base as CFDictionary)
        guard !value.isEmpty else { return }
        var item = base
        item[kSecValueData as String] = Data(value.utf8)
        SecItemAdd(item as CFDictionary, nil)
    }
}

struct WakeWordView: View {
    @Bindable var model: AppModel
    @Bindable var wakeWord: WakeWordService
    @State private var accessKey = ""
    @State private var importingKeyword = false

    init(model: AppModel) {
        self.model = model
        self.wakeWord = model.wakeWord
    }

    var body: some View {
        Form {
            Section("Picovoice") {
                SecureField("AccessKey", text: $accessKey)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                Link(
                    "Get a free AccessKey",
                    destination: URL(string: "https://console.picovoice.ai/")!
                )
                Text("The AccessKey is stored in the iPhone Keychain.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Wake word") {
                Toggle("Enable wake word", isOn: $wakeWord.isEnabled)
                Picker("Keyword", selection: $wakeWord.keyword) {
                    ForEach(WakeKeyword.allCases) {
                        Text($0.rawValue).tag($0)
                    }
                }
                if wakeWord.keyword == .custom {
                    Button("Import iOS .ppn Keyword") {
                        importingKeyword = true
                    }
                    if !wakeWord.customKeywordName.isEmpty {
                        Text(wakeWord.customKeywordName)
                            .foregroundStyle(.secondary)
                    }
                }
                VStack(alignment: .leading) {
                    Text("Sensitivity \(wakeWord.sensitivity.formatted(.number.precision(.fractionLength(2))))")
                    Slider(value: $wakeWord.sensitivity, in: 0...1, step: 0.05)
                }
            }

            Section {
                Button(wakeWord.isListening ? "Stop Wake Mode" : "Save and Start Wake Mode") {
                    wakeWord.accessKey = accessKey
                    model.applyWakeWordSettings()
                }
                .buttonStyle(.borderedProminent)
                Text(wakeWord.statusMessage)
                    .foregroundStyle(.secondary)
            }

            Section("How it works") {
                Text("Wake audio stays on the iPhone. After detection, the app records one request, answers it, then returns to wake-word mode.")
                Text("Wake mode requires the app to remain running with an active background microphone session. Force-quitting stops it.")
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
        .navigationTitle("Wake Word")
        .navigationBarTitleDisplayMode(.inline)
        .task { accessKey = wakeWord.accessKey }
        .fileImporter(
            isPresented: $importingKeyword,
            allowedContentTypes: [UTType(filenameExtension: "ppn") ?? .data]
        ) { result in
            guard case .success(let url) = result else { return }
            do {
                try wakeWord.importCustomKeyword(from: url)
            } catch {
                wakeWord.statusMessage = error.localizedDescription
            }
        }
    }
}
