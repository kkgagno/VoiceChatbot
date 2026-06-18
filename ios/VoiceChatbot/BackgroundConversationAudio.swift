import AVFAudio
import Foundation
import MediaPlayer

@MainActor
final class BackgroundConversationAudio: NSObject, AVAudioPlayerDelegate {
    var onSpeechSegment: (@Sendable (Data) -> Void)?
    var onPlaybackFinished: (() -> Void)?
    var onRemotePause: (() -> Void)?
    var onRemoteResume: (() -> Void)?
    var onRemoteStop: (() -> Void)?

    private let engine = AVAudioEngine()
    private var player: AVAudioPlayer?
    private var isPaused = false
    private var inputTapInstalled = false
    private lazy var speechDetector = BackgroundSpeechDetector { [weak self] data in
        Task { @MainActor in
            self?.onSpeechSegment?(data)
        }
    }

    override init() {
        super.init()
        configureRemoteCommands()
        observeInterruptions()
    }

    func requestPermission() async -> Bool {
        await AVAudioApplication.requestRecordPermission()
    }

    func startListening() throws {
        isPaused = false
        try configureSession()

        let input = engine.inputNode
        speechDetector.resume()
        guard !engine.isRunning else { return }

        removeInputTap()
        engine.reset()
        let detector = speechDetector
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
        stopEngine()
        try configurePlaybackSession()
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
            self?.player = nil
            self?.onPlaybackFinished?()
        }
    }

    private func configureSession() throws {
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(
            .playAndRecord,
            mode: .voiceChat,
            options: [.defaultToSpeaker, .allowBluetooth]
        )
        try session.setPreferredSampleRate(48_000)
        try session.setActive(true)
    }

    private func configurePlaybackSession() throws {
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playback, mode: .spokenAudio, options: [])
        try session.setActive(true)
    }

    private func stopEngine() {
        removeInputTap()
        if engine.isRunning {
            engine.stop()
        }
        engine.reset()
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
    private let onSegment: @Sendable (Data) -> Void

    private var samples = [Float]()
    private var preRollSamples = [Float]()
    private var sampleRate = 16_000.0
    private var heardSpeech = false
    private var silenceFrames = 0
    private var suspended = true

    private let startThreshold: Float = 0.014
    private let stopThreshold: Float = 0.009
    private let preRollSeconds = 0.65
    private let silenceSeconds = 2.4
    private let minimumSpeechSeconds = 0.35
    private let maximumSegmentSeconds = 300.0

    init(onSegment: @escaping @Sendable (Data) -> Void) {
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

            if !heardSpeech {
                appendToPreRoll(buffer)
                guard rms >= startThreshold else { return }
                heardSpeech = true
                samples = preRollSamples
                preRollSamples.removeAll(keepingCapacity: true)
            } else {
                samples.append(contentsOf: buffer)
            }

            silenceFrames = rms < stopThreshold ? silenceFrames + buffer.count : 0
            let recordedSeconds = Double(samples.count) / sampleRate
            let silentSeconds = Double(silenceFrames) / sampleRate
            if recordedSeconds >= maximumSegmentSeconds ||
                (recordedSeconds >= minimumSpeechSeconds && silentSeconds >= silenceSeconds) {
                finishSegment()
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
