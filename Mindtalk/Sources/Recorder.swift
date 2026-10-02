import AVFoundation
import CoreAudio
import os

/// Records the default input into 16 kHz mono Float samples — the format
/// Pianissimo expects. Samples live only in memory.
final class Recorder: @unchecked Sendable {
    var onLevel: (@Sendable (Float) -> Void)?
    /// The input went away mid-recording (AirPods disconnected, device changed).
    var onInterrupted: (@Sendable () -> Void)?
    private var configObserver: NSObjectProtocol?

    private var engine = AVAudioEngine()
    /// The device the engine records from — the chosen one, or whichever was
    /// the system input when the engine was made.
    private var engineDevice: AudioDeviceID?
    private let lock = NSLock()
    private var samples: [Float] = []
    private var running = false
    /// False for the level preview in the mic picker — nothing is kept.
    private let keepSamples: Bool

    init(keepSamples: Bool = true) {
        self.keepSamples = keepSamples
    }

    static let targetFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000,
                                            channels: 1, interleaved: false)!

    static var micAuthorized: Bool { AVCaptureDevice.authorizationStatus(for: .audio) == .authorized }

    static func requestMicrophone() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return true
        case .notDetermined: return await AVCaptureDevice.requestAccess(for: .audio)
        default: return false
        }
    }

    /// Starts recording from `device`, or from the system input when nil.
    func start(device: AudioDeviceID? = nil) throws {
        lock.lock(); samples = []; lock.unlock()
        guard !running else { return }
        // "Automatiskt" follows the system input, which changes when you plug in
        // AirPods; a reused engine could stay on the old device.
        let target = device ?? Microphones.defaultInputID()
        do {
            try open(device, target: target, fresh: target != engineDevice)
        } catch {
            // AirPods drop to their headset format (24 kHz) the moment their mic
            // opens, so the format just read can already be stale. A fresh engine
            // reads the new one.
            Self.log.error("start failed, retrying with a fresh engine: \(error.localizedDescription, privacy: .public)")
            try open(device, target: target, fresh: true)
        }
        running = true
        // The engine stops by itself when its device changes or disappears.
        configObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil) { [weak self] _ in
            // Also posted when the engine merely settles its format at start — only a
            // stopped engine means the input is really gone.
            guard let self, self.running, !self.engine.isRunning else { return }
            self.engineDevice = nil          // make a fresh engine next time
            self.onInterrupted?()
        }
    }

    private func open(_ device: AudioDeviceID?, target: AudioDeviceID?, fresh: Bool) throws {
        if fresh {
            // A fresh engine picks up the new device's format cleanly.
            engine = AVAudioEngine()
            engineDevice = target
            if var id = device, let unit = engine.inputNode.audioUnit,
               AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                    &id, UInt32(MemoryLayout<AudioDeviceID>.size)) != noErr {
                engineDevice = Microphones.defaultInputID()   // couldn't switch — it's on the system input
            }
        }
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0,
              let converter = AVAudioConverter(from: format, to: Self.targetFormat) else {
            engineDevice = nil
            throw Self.noMicrophone
        }
        // AVAudioEngine raises an Objective-C exception, not a Swift error, when the
        // device's format no longer matches — uncaught, it would quit the app.
        var tapError: NSError?
        let tapped = MTCatch({
            input.installTap(onBus: 0, bufferSize: 1024, format: format,
                             block: Self.tapBlock(for: self, converter: converter))
        }, &tapError)
        guard tapped else {
            Self.log.error("installTap: \(tapError?.localizedFailureReason ?? "?", privacy: .public)")
            engineDevice = nil
            throw Self.noMicrophone
        }
        engine.prepare()
        do {
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            engineDevice = nil
            throw error
        }
    }

    private static let noMicrophone = NSError(
        domain: "Mindtalk", code: 1,
        userInfo: [NSLocalizedDescriptionKey: String(localized: "Hittar ingen fungerande mikrofon.")])

    private static let log = Logger(subsystem: "ai.mindact.mindtalk", category: "Recorder")

    /// Stops the mic and hands back everything recorded since `start()`.
    func stop() -> [Float] {
        if let configObserver { NotificationCenter.default.removeObserver(configObserver) }
        configObserver = nil
        if running {
            engine.inputNode.removeTap(onBus: 0)
            engine.stop()
            running = false
        }
        lock.lock(); defer { lock.unlock() }
        let out = samples
        samples = []
        return out
    }

    /// Reads an audio file as 16 kHz mono (for the self-test).
    static func load(_ url: URL) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        guard let input = AVAudioPCMBuffer(pcmFormat: file.processingFormat,
                                           frameCapacity: AVAudioFrameCount(file.length)),
              let converter = AVAudioConverter(from: file.processingFormat, to: targetFormat) else { return [] }
        try file.read(into: input)
        let ratio = targetFormat.sampleRate / file.processingFormat.sampleRate
        guard let out = AVAudioPCMBuffer(pcmFormat: targetFormat,
                                         frameCapacity: AVAudioFrameCount(Double(input.frameLength) * ratio) + 1024)
        else { return [] }
        var fed = false
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if fed { status.pointee = .endOfStream; return nil }
            fed = true
            status.pointee = .haveData
            return input
        }
        if let error { throw error }
        return Array(UnsafeBufferPointer(start: out.floatChannelData![0], count: Int(out.frameLength)))
    }

    // Built outside any actor so the tap (which runs on the audio thread) isn't
    // treated as main-actor code.
    private static func tapBlock(for recorder: Recorder, converter: AVAudioConverter) -> AVAudioNodeTapBlock {
        { buffer, _ in
            let ratio = targetFormat.sampleRate / buffer.format.sampleRate
            let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 32
            guard let out = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else { return }
            var fed = false
            var error: NSError?
            converter.convert(to: out, error: &error) { _, status in
                if fed { status.pointee = .noDataNow; return nil }
                fed = true
                status.pointee = .haveData
                return buffer
            }
            guard error == nil, let data = out.floatChannelData?[0] else { return }
            let chunk = Array(UnsafeBufferPointer(start: data, count: Int(out.frameLength)))
            var sum: Float = 0
            for v in chunk { sum += v * v }
            let rms = chunk.isEmpty ? 0 : (sum / Float(chunk.count)).squareRoot()
            if recorder.keepSamples { recorder.lock.lock(); recorder.samples += chunk; recorder.lock.unlock() }
            recorder.onLevel?(min(1, rms * 12))
        }
    }
}
