import Foundation

struct ServerProfile: Codable, Equatable {
    var name = "Home PC"
    var localURL = "https://192.168.1.50:5100"
    var vpnURL = "https://10.8.0.1:5100"
    var pin = ""
    var pinnedCertificateDER: Data?

    private enum CodingKeys: String, CodingKey {
        case name, localURL, vpnURL, pin, pinnedCertificateDER
        case legacyBaseURL = "baseURL"
    }

    init() {}

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        name = try container.decodeIfPresent(String.self, forKey: .name) ?? "Home PC"
        localURL = try container.decodeIfPresent(String.self, forKey: .localURL)
            ?? container.decodeIfPresent(String.self, forKey: .legacyBaseURL)
            ?? "https://192.168.1.50:5100"
        vpnURL = try container.decodeIfPresent(String.self, forKey: .vpnURL) ?? "https://10.8.0.1:5100"
        pin = try container.decodeIfPresent(String.self, forKey: .pin) ?? ""
        pinnedCertificateDER = try container.decodeIfPresent(Data.self, forKey: .pinnedCertificateDER)
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(name, forKey: .name)
        try container.encode(localURL, forKey: .localURL)
        try container.encode(vpnURL, forKey: .vpnURL)
        try container.encode(pin, forKey: .pin)
        try container.encodeIfPresent(pinnedCertificateDER, forKey: .pinnedCertificateDER)
    }

    var endpointCandidates: [(name: String, url: String)] {
        var seen = Set<String>()
        return [("Home Wi-Fi", localURL), ("VPN", vpnURL)].compactMap { name, rawURL in
            let url = rawURL.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !url.isEmpty, seen.insert(url).inserted else { return nil }
            return (name, url)
        }
    }
}

struct ServerStatus: Decodable {
    let ok: Bool
    let requiresPin: Bool
    let activeProvider: String
    let activeModel: String
    let activeEndpoint: String
}

struct TranscriptionResponse: Decodable {
    let transcript: String
}

struct PendingAttachment: Identifiable, Equatable {
    enum Kind {
        case image
        case document
        case audio
    }

    let id = UUID()
    let name: String
    let mimeType: String
    let data: Data
    let kind: Kind

    var sizeLabel: String {
        ByteCountFormatter.string(fromByteCount: Int64(data.count), countStyle: .file)
    }
}

enum ComfyAction: String, CaseIterable, Identifiable {
    case createImage = "New Image"
    case editImage = "Edit Image"
    case createVideo = "Image to Video"
    case createVideoWithAudio = "Image + Audio Video"

    var id: Self { self }

    var systemImage: String {
        switch self {
        case .createImage: "photo.badge.plus"
        case .editImage: "wand.and.stars"
        case .createVideo: "video.badge.plus"
        case .createVideoWithAudio: "waveform.badge.plus"
        }
    }

    func command(prompt: String, seconds: Int) -> String {
        switch self {
        case .createImage:
            "create an image of \(prompt)"
        case .editImage:
            "edit this image \(prompt)"
        case .createVideo, .createVideoWithAudio:
            "create a \(seconds) second video \(prompt)"
        }
    }
}

struct ComfyOutput {
    let message: String
    let imageData: Data?
    let videoURL: URL?
}

struct AssistantResponse: Decodable {
    let transcript: String
    let response: String
    let audioURL: String?
    let imageURL: String?
    let videoURL: String?
    let activeDocumentCount: Int
    let activeProvider: String?
    let activeModel: String?
    let activeEndpoint: String?

    enum CodingKeys: String, CodingKey {
        case transcript, response
        case audioURL = "audioUrl"
        case imageURL = "imageUrl"
        case videoURL = "videoUrl"
        case activeDocumentCount, activeProvider, activeModel, activeEndpoint
    }
}

struct ChatEntry: Identifiable, Equatable {
    enum Role {
        case user
        case assistant
        case system
    }

    let id = UUID()
    let role: Role
    let text: String
    var audioURL: String? = nil
}

enum ConversationState: Equatable {
    case idle
    case listening
    case transcribing
    case thinking
    case speaking
    case paused
    case failed(String)

    var label: String {
        switch self {
        case .idle: "Ready"
        case .listening: "Listening"
        case .transcribing: "Transcribing"
        case .thinking: "Thinking"
        case .speaking: "Speaking"
        case .paused: "Paused"
        case .failed(let message): message
        }
    }
}

enum ActiveSessionMode: String, CaseIterable, Identifiable {
    case conversation = "Conversation"
    case transcription = "Transcription"

    var id: Self { self }
}
