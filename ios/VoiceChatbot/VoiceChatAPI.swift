import CryptoKit
import Foundation
import Security

enum APIError: LocalizedError {
    case invalidServerURL
    case invalidResponse
    case server(Int, String)
    case certificateRequired
    case certificateMismatch

    var errorDescription: String? {
        switch self {
        case .invalidServerURL: "The server URL is invalid."
        case .invalidResponse: "The server returned an invalid response."
        case .server(let status, let text): "Server error \(status): \(text)"
        case .certificateRequired: "Import the PC server certificate before connecting."
        case .certificateMismatch: "The server certificate does not match the imported certificate."
        }
    }
}

final class PinnedCertificateDelegate: NSObject, URLSessionDelegate, @unchecked Sendable {
    private let pinnedDigest: Data?

    init(certificateDER: Data?) {
        pinnedDigest = certificateDER.map { Data(SHA256.hash(data: $0)) }
    }

    func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust,
              let certificate = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let leaf = certificate.first
        else {
            completionHandler(.performDefaultHandling, nil)
            return
        }

        guard let pinnedDigest else {
            completionHandler(.performDefaultHandling, nil)
            return
        }

        let serverDER = SecCertificateCopyData(leaf) as Data
        let serverDigest = Data(SHA256.hash(data: serverDER))
        guard serverDigest == pinnedDigest else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }

        completionHandler(.useCredential, URLCredential(trust: trust))
    }
}

actor VoiceChatAPI {
    private let profile: ServerProfile
    private let baseURL: String
    private let session: URLSession

    init(profile: ServerProfile, baseURL: String, probeMode: Bool = false) {
        self.profile = profile
        self.baseURL = baseURL
        let configuration = probeMode
            ? URLSessionConfiguration.ephemeral
            : URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = probeMode ? 8 : 90
        configuration.timeoutIntervalForResource = probeMode ? 10 : 300
        configuration.waitsForConnectivity = true
        configuration.allowsCellularAccess = true
        configuration.allowsExpensiveNetworkAccess = true
        configuration.allowsConstrainedNetworkAccess = true
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        session = URLSession(
            configuration: configuration,
            delegate: PinnedCertificateDelegate(certificateDER: profile.pinnedCertificateDER),
            delegateQueue: nil
        )
    }

    func status() async throws -> ServerStatus {
        try await request(path: "/api/status")
    }

    func transcribe(wav: Data) async throws -> String {
        let result: TranscriptionResponse = try await uploadWav(wav, path: "/api/transcribe")
        return result.transcript.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func detectSpeech(wav: Data) async throws -> Bool {
        let result: SpeechDetectionResponse = try await uploadWav(wav, path: "/api/vad")
        return result.speech
    }

    private func uploadWav<T: Decodable>(_ wav: Data, path: String) async throws -> T {
        let boundary = UUID().uuidString
        var body = Data()
        body.append("--\(boundary)\r\n")
        body.append("Content-Disposition: form-data; name=\"audio\"; filename=\"iphone.wav\"\r\n")
        body.append("Content-Type: audio/wav\r\n\r\n")
        body.append(wav)
        body.append("\r\n--\(boundary)--\r\n")

        var request = try makeRequest(path: path, method: "POST")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = body
        return try await perform(request)
    }

    func respond(to text: String, keepDocumentsActive: Bool = false) async throws -> AssistantResponse {
        var request = try makeRequest(path: "/api/respond", method: "POST")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(
            TextRequest(text: text, keepDocumentsActive: keepDocumentsActive)
        )
        return try await perform(request)
    }

    func sendMessage(
        text: String,
        attachments: [PendingAttachment],
        keepDocumentsActive: Bool
    ) async throws -> AssistantResponse {
        let boundary = "VoiceChatbot-\(UUID().uuidString)"
        var body = Data()
        body.appendMultipartField(name: "text", value: text, boundary: boundary)
        body.appendMultipartField(
            name: "keepDocumentsActive",
            value: keepDocumentsActive ? "true" : "false",
            boundary: boundary
        )
        for attachment in attachments {
            body.appendMultipartFile(
                name: "files",
                filename: attachment.name,
                mimeType: attachment.mimeType,
                data: attachment.data,
                boundary: boundary
            )
        }
        body.append("--\(boundary)--\r\n")

        var request = try makeRequest(path: "/api/message", method: "POST")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = body
        return try await perform(request)
    }

    func speak(_ text: String) async throws -> String {
        var request = try makeRequest(path: "/api/speak", method: "POST")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(SpeakRequest(text: text))
        let result: SpeakResponse = try await perform(request)
        return result.audioURL
    }

    func audioData(relativePath: String) async throws -> Data {
        try await mediaData(relativePath: relativePath)
    }

    func mediaData(relativePath: String) async throws -> Data {
        let request = try makeRequest(path: relativePath, method: "GET")
        let (data, response) = try await session.data(for: request)
        try validate(response: response, data: data)
        return data
    }

    private func request<T: Decodable>(path: String) async throws -> T {
        try await perform(makeRequest(path: path, method: "GET"))
    }

    private func perform<T: Decodable>(_ request: URLRequest) async throws -> T {
        let (data, response) = try await session.data(for: request)
        try validate(response: response, data: data)
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func makeRequest(path: String, method: String) throws -> URLRequest {
        guard var base = URL(string: baseURL) else {
            throw APIError.invalidServerURL
        }
        if !path.isEmpty {
            base.append(path: path.trimmingCharacters(in: CharacterSet(charactersIn: "/")))
        }
        var request = URLRequest(url: base)
        request.httpMethod = method
        if !profile.pin.isEmpty {
            request.setValue(profile.pin, forHTTPHeaderField: "X-Phone-Remote-Pin")
        }
        return request
    }

    private func validate(response: URLResponse, data: Data) throws {
        guard let http = response as? HTTPURLResponse else {
            throw APIError.invalidResponse
        }
        guard (200..<300).contains(http.statusCode) else {
            throw APIError.server(http.statusCode, String(data: data, encoding: .utf8) ?? "")
        }
    }
}

private struct TextRequest: Encodable {
    let text: String
    let keepDocumentsActive: Bool
}

private struct SpeechDetectionResponse: Decodable {
    let speech: Bool
}

private struct SpeakRequest: Encodable {
    let text: String
}

private struct SpeakResponse: Decodable {
    let audioURL: String

    enum CodingKeys: String, CodingKey {
        case audioURL = "audioUrl"
    }
}

private extension Data {
    mutating func append(_ string: String) {
        append(Data(string.utf8))
    }

    mutating func appendMultipartField(name: String, value: String, boundary: String) {
        append("--\(boundary)\r\n")
        append("Content-Disposition: form-data; name=\"\(name)\"\r\n\r\n")
        append(value)
        append("\r\n")
    }

    mutating func appendMultipartFile(
        name: String,
        filename: String,
        mimeType: String,
        data: Data,
        boundary: String
    ) {
        let safeFilename = filename
            .replacingOccurrences(of: "\"", with: "")
            .replacingOccurrences(of: "\r", with: "")
            .replacingOccurrences(of: "\n", with: "")
        append("--\(boundary)\r\n")
        append("Content-Disposition: form-data; name=\"\(name)\"; filename=\"\(safeFilename)\"\r\n")
        append("Content-Type: \(mimeType)\r\n\r\n")
        append(data)
        append("\r\n")
    }
}
