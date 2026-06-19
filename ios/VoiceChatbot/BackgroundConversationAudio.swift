import AVFAudio
import Foundation
import MediaPlayer
import UIKit

@MainActor
final class BackgroundConversationAudio: NSObject, AVAudioPlayerDelegate {
    var onSpeechSegment: (@Sendable (Data) -> Void)?
    var onSpeechCandidate: (@Sendable (Data) async -> Bool)?
    var onPlaybackFinished: (() -> Void)?
    var onRemotePause: (() -> Void)?
    var onRemoteResume: (() -> Void)?
    var onRemoteStop: (() -> Void)?

    private let engine = AVAudioEngine()
    private var player: AVAudioPlayer?
    private var isPaused = false
    private var inputTapInstalled = false
    private var voiceProcessingEnabled = false
    private lazy var speechDetector = BackgroundSpeechDetector(
        onCandidate: { [weak self] data, completion in
            Task { @MainActor in
                guard let detector = self?.onSpeechCandidate else {
                    completion(true)
                    return
                }
                completion(await detector(data))
            }
        },
        onSegment: { [weak self] data in
            Task { @MainActor in
                self?.onSpeechSegment?(data)
            }
        }
    )

    override init() {
        super.init()
        configureRemoteCommands()
        observeInterruptions()
        observeRouteChanges()
    }

    func requestPermission() async -> Bool {
        await AVAudioApplication.requestRecordPermission()
    }

    func startListening() throws {
        isPaused = false
        try configureSession()

        let input = engine.inputNode
        try enableVoiceProcessingIfAvailable()
        let detector = speechDetector
        detector.resume()
        if engine.isRunning {
            updateNowPlaying(active: true)
            return
        }

        removeInputTap()
        engine.reset()
        input.installTap(onBus: 0, bufferSize: 2_048, format: nil) { buffer, _ in
            guard let channel = buffer.floatChannelData?.pointee else { return }
            let values = Array(UnsafeBufferPointer(start: channel, count: Int(buffer.frameLength)))
            detector.consume(values, sampleRate: buffer.format.sampleRate)
        }
        inputTapInstalled = true

        engine.prepare()
        do {
            try engine.start()
        } catch {
            removeInputTap()
            throw error
        }
        updateNowPlaying(active: true)
    }

    func pause() {
        isPaused = true
        speechDetector.suspendAndReset()
        stopEngine()
        updateNowPlaying(active: false)
    }

    func resume() throws {
        try startListening()
    }

    func stop() {
        isPaused = false
        speechDetector.suspendAndReset(keepingCapacity: false)
        stopEngine()
        player?.stop()
        player = nil
        updateNowPlaying(active: false)
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }

    func play(_ data: Data) throws {
        speechDetector.suspendAndReset()
        let session = AVAudioSession.sharedInstance()
        if UIDevice.current.userInterfaceIdiom == .pad {
            stopEngine()
            try? engine.inputNode.setVoiceProcessingEnabled(false)
            voiceProcessingEnabled = false
            try session.setActive(false, options: .notifyOthersOnDeactivation)
            try session.setCategory(.playback, mode: .spokenAudio, options: [])
            try session.setActive(true)
        } else {
            try session.overrideOutputAudioPort(.speaker)
        }
        let audioPlayer = try AVAudioPlayer(data: data)
        audioPlayer.delegate = self
        audioPlayer.volume = 1
        audioPlayer.prepareToPlay()
        player = audioPlayer
        guard audioPlayer.play() else {
            throw AudioError.playbackFailed
        }
    }

    nonisolated func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        Task { @MainActor [weak self] in
            guard let self else { return }
            self.player = nil
            if !self.isPaused {
                if UIDevice.current.userInterfaceIdiom == .pad {
                    let session = AVAudioSession.sharedInstance()
                    try? session.setActive(false, options: .notifyOthersOnDeactivation)
                }
            }
            self.onPlaybackFinished?()
        }
    }

    private func configureSession() throws {
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(
            .playAndRecord,
            mode: .default,
            options: [.defaultToSpeaker, .allowBluetooth]
        )
        try session.setPreferredSampleRate(48_000)
        try session.setActive(true)
        try session.overrideOutputAudioPort(.speaker)
    }

    private func stopEngine() {
        removeInputTap()
        if engine.isRunning {
            engine.stop()
        }
        engine.reset()
    }

    private func enableVoiceProcessingIfAvailable() {
        guard !voiceProcessingEnabled else { return }
        let input = engine.inputNode
        do {
            try input.setVoiceProcessingEnabled(true)
            voiceProcessingEnabled = true
        } catch {
            // Some Bluetooth and external audio routes do not support Apple's
            // voice-processing unit. Continue with the native route there.
            try? input.setVoiceProcessingEnabled(false)
            voiceProcessingEnabled = false
        }
    }

    private func removeInputTap() {
        guard inputTapInstalled else { return }
        engine.inputNode.removeTap(onBus: 0)
        inputTapInstalled = false
    }

    private func observeInterruptions() {
        NotificationCenter.default.addObserver(
            forName: AVAudioSession.interruptionNotification,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            guard let self,
                  let raw = notification.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt,
                  let type = AVAudioSession.InterruptionType(rawValue: raw)
            else { return }

            Task { @MainActor in
                if type == .ended, !self.isPaused {
                    try? self.startListening()
                } else if type == .began {
                    self.stopEngine()
                }
            }
        }
    }

    private func observeRouteChanges() {
        NotificationCenter.default.addObserver(
            forName: AVAudioSession.routeChangeNotification,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            guard let self, !self.isPaused, self.player == nil else { return }
            let rawReason = notification.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt
            let reason = rawReason.flatMap(AVAudioSession.RouteChangeReason.init(rawValue:))
            switch reason {
            case .newDeviceAvailable, .oldDeviceUnavailable, .noSuitableRouteForCategory, .wakeFromSleep:
                break
            default:
                return
            }

            Task { @MainActor in
                self.stopEngine()
                try? await Task.sleep(for: .milliseconds(150))
                try? self.startListening()
            }
        }
    }

    private func configureRemoteCommands() {
        let center = MPRemoteCommandCenter.shared()
        center.pauseCommand.addTarget { [weak self] _ in
            Task { @MainActor in
                self?.pause()
                self?.onRemotePause?()
            }
            return .success
        }
        center.playCommand.addTarget { [weak self] _ in
            Task { @MainActor in
                try? self?.resume()
                self?.onRemoteResume?()
            }
            return .success
        }
        center.stopCommand.addTarget { [weak self] _ in
            Task { @MainActor in
                self?.stop()
                self?.onRemoteStop?()
            }
            return .success
        }
    }

    func updateNowPlaying(active: Bool, title: String = "Active conversation") {
        MPNowPlayingInfoCenter.default().nowPlayingInfo = active ? [
            MPMediaItemPropertyTitle: "Voice Chatbot",
            MPMediaItemPropertyArtist: title,
            MPNowPlayingInfoPropertyPlaybackRate: 1
        ] : nil
    }
}

private final class BackgroundSpeechDetector: @unchecked Sendable {
    private let queue = DispatchQueue(
        label: "com.keithgagnon.VoiceChatbot.speech-detector",
        qos: .userInitiated
    )
    private let onCandidate: @Sendable (Data, @escaping @Sendable (Bool) -> Void) -> Void
    private let onSegment: @Sendable (Data) -> Void

    private var samples = [Float]()
    private var preRollSamples = [Float]()
    private var sampleRate = 16_000.0
    private var heardSpeech = false
    private var silenceFrames = 0
    private var suspended = true
    private var noiseFloor: Float = 0.004
    private var speechConfirmed = false
    private var candidatePending = false
    private var candidateGeneration = 0

    private let preRollSeconds = 1.0
    private let silenceSeconds = 2.4
    private let minimumSpeechSeconds = 0.35
    private let maximumSegmentSeconds = 300.0

    init(
        onCandidate: @escaping @Sendable (Data, @escaping @Sendable (Bool) -> Void) -> Void,
        onSegment: @escaping @Sendable (Data) -> Void
    ) {
        self.onCandidate = onCandidate
        self.onSegment = onSegment
    }

    func resume() {
        queue.sync {
            suspended = false
            reset(keepingCapacity: true)
        }
    }

    func suspendAndReset(keepingCapacity: Bool = true) {
        queue.sync {
            suspended = true
            reset(keepingCapacity: keepingCapacity)
        }
    }

    func consume(_ buffer: [Float], sampleRate: Double) {
        queue.async { [self] in
            guard !suspended, !buffer.isEmpty, sampleRate > 0 else { return }
            self.sampleRate = sampleRate
            let rms = sqrt(buffer.reduce(0) { $0 + $1 * $1 } / Float(buffer.count))
            let startThreshold = max(0.009, noiseFloor * 2.4)

            if !heardSpeech {
                appendToPreRoll(buffer)
                if rms < startThreshold {
                    noiseFloor = noiseFloor * 0.97 + rms * 0.03
                    return
                }
                heardSpeech = true
                speechConfirmed = false
                candidatePending = false
                candidateGeneration += 1
                samples = preRollSamples
                preRollSamples.removeAll(keepingCapacity: true)
            } else {
                samples.append(contentsOf: buffer)
            }

            let recordedSeconds = Double(samples.count) / sampleRate
            if !speechConfirmed, !candidatePending, recordedSeconds >= 0.75 {
                requestSpeechConfirmation()
            }

            let stopThreshold = max(0.005, noiseFloor * 1.45)
            let containsSpeech = speechConfirmed && rms >= stopThreshold
            silenceFrames = containsSpeech ? 0 : silenceFrames + buffer.count
            let silentSeconds = Double(silenceFrames) / sampleRate
            if recordedSeconds >= maximumSegmentSeconds ||
                (speechConfirmed && recordedSeconds >= minimumSpeechSeconds && silentSeconds >= silenceSeconds) {
                finishSegment()
            }
        }
    }

    private func requestSpeechConfirmation() {
        candidatePending = true
        let generation = candidateGeneration
        let candidate = WAVEncoder.encode(samples: samples, sourceRate: sampleRate)
        onCandidate(candidate) { [weak self] containsSpeech in
            self?.queue.async { [weak self] in
                guard let self, generation == self.candidateGeneration, self.heardSpeech else { return }
                self.candidatePending = false
                if containsSpeech {
                    self.speechConfirmed = true
                    self.silenceFrames = 0
                } else if Double(self.samples.count) / self.sampleRate >= 1.1 {
                    self.reset(keepingCapacity: true)
                    self.candidateGeneration += 1
                }
            }
        }
    }

    private func finishSegment() {
        let completed = samples
        reset(keepingCapacity: true)
        guard Double(completed.count) / sampleRate >= minimumSpeechSeconds else { return }
        suspended = true
        onSegment(WAVEncoder.encode(samples: completed, sourceRate: sampleRate))
    }

    private func appendToPreRoll(_ buffer: [Float]) {
        preRollSamples.append(contentsOf: buffer)
        let maximumCount = max(1, Int(sampleRate * preRollSeconds))
        if preRollSamples.count > maximumCount {
            preRollSamples.removeFirst(preRollSamples.count - maximumCount)
        }
    }

    private func reset(keepingCapacity: Bool) {
        samples.removeAll(keepingCapacity: keepingCapacity)
        preRollSamples.removeAll(keepingCapacity: keepingCapacity)
        heardSpeech = false
        silenceFrames = 0
        speechConfirmed = false
        candidatePending = false
    }
}

private enum AudioError: LocalizedError {
    case playbackFailed

    var errorDescription: String? {
        "The response audio could not start playing."
    }
}

enum WAVEncoder {
    static func encode(samples: [Float], sourceRate: Double) -> Data {
        let targetRate = 16_000.0
        let ratio = sourceRate / targetRate
        let count = Int(Double(samples.count) / ratio)
        var pcm = [Int16]()
        pcm.reserveCapacity(count)
        for index in 0..<count {
            let sourceIndex = min(Int(Double(index) * ratio), samples.count - 1)
            let value = max(-1, min(1, samples[sourceIndex]))
            pcm.append(Int16(value * Float(Int16.max)))
        }

        var data = Data()
        data.appendASCII("RIFF")
        data.appendLE(UInt32(36 + pcm.count * 2))
        data.appendASCII("WAVEfmt ")
        data.appendLE(UInt32(16))
        data.appendLE(UInt16(1))
        data.appendLE(UInt16(1))
        data.appendLE(UInt32(targetRate))
        data.appendLE(UInt32(targetRate * 2))
        data.appendLE(UInt16(2))
        data.appendLE(UInt16(16))
        data.appendASCII("data")
        data.appendLE(UInt32(pcm.count * 2))
        pcm.forEach { data.appendLE($0) }
        return data
    }
}

private extension Data {
    mutating func appendASCII(_ string: String) {
        append(Data(string.utf8))
    }

    mutating func appendLE<T: FixedWidthInteger>(_ value: T) {
        var littleEndian = value.littleEndian
        Swift.withUnsafeBytes(of: &littleEndian) { append(contentsOf: $0) }
    }
}
