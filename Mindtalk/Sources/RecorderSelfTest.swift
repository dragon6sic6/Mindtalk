#if DEBUG
import CoreAudio
import Foundation

/// Exercises the live microphone path, including the format negotiation that
/// file transcription never reaches. Audio stays in memory and is discarded.
@MainActor
enum RecorderSelfTest {
    static func run(arguments: [String]) async -> Int32 {
        do {
            let cycles = try argument("--record-test-cycles", in: arguments).map { value in
                guard let count = Int(value), count > 0 else {
                    throw Failure("--record-test-cycles requires a positive integer")
                }
                return count
            } ?? 2
            var targets: [(name: String, id: AudioDeviceID?)] = []
            let microphones = Microphones.shared
            if let builtIn = microphones.builtIn {
                targets.append(("built-in (\(builtIn.id))", builtIn.id))
            }
            targets.append(("system default", nil))
            if let value = try argument("--record-test-device", in: arguments) {
                guard let id = AudioDeviceID(value),
                      let device = microphones.devices.first(where: { $0.id == id && !$0.isVirtual }) else {
                    throw Failure("--record-test-device requires an available physical input device ID")
                }
                if !targets.contains(where: { $0.id == id }) {
                    targets.append(("\(device.name) (\(id))", id))
                }
            }
            print("Recorder self-test: microphone permission is required; no audio is saved.")
            guard await Recorder.requestMicrophone() else {
                throw Failure("Microphone permission was denied")
            }
            let recorder = Recorder()
            try await failedStartThenRetry(recorder, target: targets[0])
            try await releaseWhileRecording(target: targets[0])
            let preview = Recorder(keepSamples: false)
            for cycle in 1...cycles {
                for target in targets {
                    try await record(recorder, target: target, label: "capture \(cycle)", keepSamples: true)
                    try await record(preview, target: target, label: "preview \(cycle)", keepSamples: false)
                }
            }
            print("Recorder self-test passed (\(cycles) cycles, \(targets.count) input selections).")
            return 0
        } catch {
            print("Recorder self-test failed: \(error.localizedDescription)")
            return 1
        }
    }

    private static func record(_ recorder: Recorder, target: (name: String, id: AudioDeviceID?),
                               label: String, keepSamples: Bool) async throws {
        let measurement = Measurement()
        recorder.onLevel = { measurement.level($0) }
        recorder.onInterrupted = { measurement.interrupted() }
        print("Starting \(label): \(target.name)")
        defer { _ = recorder.stop() }
        try recorder.start(device: target.id)
        // Allow slow devices to open, then measure a second of actual capture.
        try await waitForAudio(measurement, label: "\(label), \(target.name)")
        try await Task.sleep(for: .milliseconds(500))
        // A repeated start must be harmless and preserve the first half's audio.
        try recorder.start(device: target.id)
        try await Task.sleep(for: .milliseconds(500))
        let stoppedAt = Date()
        let samples = recorder.stop()
        let result = measurement.snapshot()
        guard !result.invalidLevel, result.interruptions == 0 else {
            throw Failure("\(label), \(target.name): invalid level or unexpected interruption")
        }
        if keepSamples {
            guard !samples.isEmpty, samples.allSatisfy(\.isFinite) else {
                throw Failure("\(label), \(target.name): missing or invalid samples")
            }
            let duration = Double(samples.count) / Recorder.targetFormat.sampleRate
            let elapsed = stoppedAt.timeIntervalSince(result.firstCallback!)
            // Tolerate audio scheduling jitter while detecting a reset on the
            // second start (which otherwise discards approximately half).
            guard duration >= elapsed * 0.75, duration <= elapsed + 1 else {
                throw Failure("\(label), \(target.name): expected continuous 16 kHz audio, got \(duration)s over \(elapsed)s")
            }
        } else if !samples.isEmpty {
            throw Failure("\(label), \(target.name): level preview retained audio")
        }
        guard recorder.stop().isEmpty else {
            throw Failure("\(label), \(target.name): second stop returned audio")
        }
        print("Passed \(label), \(target.name): \(samples.count) samples, \(result.callbacks) level callbacks")
    }

    private static func failedStartThenRetry(_ recorder: Recorder,
                                            target: (name: String, id: AudioDeviceID?)) async throws {
        let measurement = Measurement()
        recorder.onLevel = { measurement.level($0) }
        defer { _ = recorder.stop() }
        var rejected = false
        do { try recorder.start(device: AudioDeviceID.max) }
        catch { rejected = true }
        guard rejected else { throw Failure("Invalid device unexpectedly started recording") }
        try await Task.sleep(for: .milliseconds(250))
        guard measurement.snapshot().callbacks == 0 else {
            throw Failure("Failed start left an active recording queue")
        }
        // Retry without stopping first: start() must clean up its own failure.
        try await record(recorder, target: target, label: "retry after failed start", keepSamples: true)
    }

    private static func releaseWhileRecording(target: (name: String, id: AudioDeviceID?)) async throws {
        let measurement = Measurement()
        var recorder: Recorder? = Recorder(keepSamples: false)
        let released = WeakRecorder(recorder)
        let owner = CallbackOwner(recorder!)
        defer { owner.stop() }
        recorder?.onLevel = { value in
            measurement.level(value)
            owner.releaseIfArmed()
        }
        try recorder?.start(device: target.id)
        // Drop the main actor's reference before arming. The next callback
        // releases the owner, making the callback's temporary reference last.
        recorder = nil
        owner.arm()
        try await waitForAudio(measurement, label: "release while recording, \(target.name)")
        let deadline = Date().addingTimeInterval(2)
        while released.value != nil, Date() < deadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        guard released.value == nil else { throw Failure("Running recorder was retained after release") }
        let callbacks = measurement.snapshot().callbacks
        try await Task.sleep(for: .milliseconds(250))
        guard measurement.snapshot().callbacks == callbacks else {
            throw Failure("Audio callbacks continued after recorder release")
        }
        print("Passed release from audio callback, \(target.name)")
    }

    private static func waitForAudio(_ measurement: Measurement, label: String) async throws {
        let deadline = Date().addingTimeInterval(5)
        while measurement.snapshot().firstCallback == nil {
            guard Date() < deadline else { throw Failure("\(label): no level callbacks") }
            try await Task.sleep(for: .milliseconds(50))
        }
    }

    private static func argument(_ flag: String, in arguments: [String]) throws -> String? {
        guard let index = arguments.firstIndex(of: flag) else { return nil }
        guard index + 1 < arguments.count else { throw Failure("Missing value after \(flag)") }
        return arguments[index + 1]
    }

    private struct Failure: LocalizedError {
        let message: String
        init(_ message: String) { self.message = message }
        var errorDescription: String? { message }
    }

    private final class WeakRecorder {
        weak var value: Recorder?
        init(_ value: Recorder?) { self.value = value }
    }

    private final class CallbackOwner: @unchecked Sendable {
        private let lock = NSLock()
        private var recorder: Recorder?
        private var armed = false
        init(_ recorder: Recorder) { self.recorder = recorder }

        func arm() {
            lock.lock(); defer { lock.unlock() }
            armed = true
        }

        func releaseIfArmed() {
            lock.lock(); defer { lock.unlock() }
            if armed { recorder = nil }
        }

        func stop() {
            lock.lock()
            let active = recorder
            recorder = nil
            lock.unlock()
            _ = active?.stop()
        }
    }

    /// Audio callbacks run off the main actor; the test reads on the main actor.
    private final class Measurement: @unchecked Sendable {
        private let lock = NSLock()
        private var callbacks = 0
        private var invalidLevel = false
        private var interruptions = 0
        private var firstCallback: Date?

        func level(_ value: Float) {
            lock.lock(); defer { lock.unlock() }
            callbacks += 1
            invalidLevel = invalidLevel || !value.isFinite || !(0...1).contains(value)
            if firstCallback == nil { firstCallback = Date() }
        }

        func interrupted() {
            lock.lock(); defer { lock.unlock() }
            interruptions += 1
        }

        func snapshot() -> (callbacks: Int, invalidLevel: Bool, interruptions: Int, firstCallback: Date?) {
            lock.lock(); defer { lock.unlock() }
            return (callbacks, invalidLevel, interruptions, firstCallback)
        }
    }
}
#endif
