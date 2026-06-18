import Foundation

struct ServerProfile: Codable, Equatable {
    var name = "Home PC"
    var baseURL = "https://192.168.1.50:5100"
    var pin = ""
    var pinnedCertificateDER: Data?
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
