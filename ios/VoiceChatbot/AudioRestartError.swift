import Foundation

enum AudioRestartError: LocalizedError {
    case failed

    var errorDescription: String? {
        "The microphone could not restart after playback. Tap Start to retry."
    }
}
