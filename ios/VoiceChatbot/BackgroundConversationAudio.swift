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
    private var samples = [Float]()
    private var sampleRate = 16_000.0
    private var heardSpeech = false
    private var silenceFrames = 0
    private var isPaused = false

    private let startThreshold: Float = 0.018
    private let stopThreshold: Float = 0.012
    private let silenceSeconds = 1.2
    private let minimumSpeechSeconds = 0.35
    private let maximumSegmentSeconds = 300.0

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
        guard !engine.isRunning else { return }

        let input = engine.inputNode
        let inputFormat = input.outputFormat(forBus: 0)
        sampleRate = inputFormat.sampleRate
        input.installTap(onBus: 0, bufferSize: 2_048, format: inputFormat) { [weak self] buffer, _ in
            guard let channel = buffer.floatChannelData?.pointee else { return }
            let values = Array(UnsafeBufferPointer(start: channel, count: Int(buffer.frameLength)))
            Task { @MainActor [weak self] in
                self?.consume(values)
            }
        }

        engine.prepare()
        try engine.start()
        updateNowPlaying(active: true)
    }

    func pause() {
        isPaused = true
        stopEngine()
        updateNowPlaying(active: false)
    }

    func resume() throws {
        try startListening()
    }

    func stop() {
        isPaused = false
        stopEngine()
        samples.removeAll(keepingCapacity: false)
        heardSpeech = false
        silenceFrames = 0
        player?.stop()
        player = nil
        updateNowPlaying(active: false)
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }

    func play(_ data: Data) throws {
        stopEngine()
        let audioPlayer = try AVAudioPlayer(data: data)
        audioPlayer.delegate = self
        audioPlayer.prepareToPlay()
        player = audioPlayer
        audioPlayer.play()
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

    private func stopEngine() {
        guard engine.isRunning else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
    }

    private func consume(_ buffer: [Float]) {
        guard !isPaused, !buffer.isEmpty else { return }
        let rms = sqrt(buffer.reduce(0) { $0 + $1 * $1 } / Float(buffer.count))

        if !heardSpeech {
            guard rms >= startThreshold else { return }
            heardSpeech = true
            samples.removeAll(keepingCapacity: true)
        }

        samples.append(contentsOf: buffer)
        silenceFrames = rms < stopThreshold ? silenceFrames + buffer.count : 0

        let recordedSeconds = Double(samples.count) / sampleRate
        let silentSeconds = Double(silenceFrames) / sampleRate
        if recordedSeconds >= maximumSegmentSeconds ||
            (recordedSeconds >= minimumSpeechSeconds && silentSeconds >= silenceSeconds) {
            finishSegment()
        }
    }

    private func finishSegment() {
        let completed = samples
        samples.removeAll(keepingCapacity: true)
        heardSpeech = false
        silenceFrames = 0
        guard Double(completed.count) / sampleRate >= minimumSpeechSeconds else { return }
        stopEngine()
        onSpeechSegment?(WAVEncoder.encode(samples: completed, sourceRate: sampleRate))
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
