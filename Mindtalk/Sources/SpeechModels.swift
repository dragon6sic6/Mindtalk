import Foundation
import CoreML
import CryptoKit
import FluidAudio

// MARK: - The speech models
//
// Both are NVIDIA Parakeet TDT 0.6B v3 (CC BY 4.0), trained further by others,
// and run fully on-device through FluidAudio's Parakeet runtime:
//
//   • Svenska — "Klang Pianissimo" by Klang AI AB (huggingface.co/KlangAI/pianissimo-sv),
//     CC BY 4.0. We run the community Core ML conversion markstrom/pianissimo-sv-coreml
//     (CC BY 4.0), compiled on the Mac at install.
//   • English + 24 språk — "Parakeet Ultra" by Moondream (huggingface.co/moondream/parakeet-ultra),
//     CC BY 4.0, as converted to Core ML by FluidInference (FluidInference/parakeet-ultra-coreml).
//
// Neither is bundled — each is downloaded once, from a pinned revision, and every
// file is checked against its sha256 so a changed upstream repo can't slip in new weights.

enum SpeechModel: String, CaseIterable, Identifiable, Codable, Sendable {
    case swedish
    case multilingual

    var id: String { rawValue }

    /// What the user picks: the language.
    var title: String {
        switch self {
        case .swedish: return String(localized: "Svenska")
        case .multilingual: return String(localized: "Flerspråkig")
        }
    }

    /// Short tag for Swedish ("SV"); the multilingual model shows a globe instead (see `ModelBadge`).
    var code: String {
        switch self {
        case .swedish: return "SV"
        case .multilingual: return "EN"
        }
    }

    var modelName: String {
        switch self {
        case .swedish: return "Klang Pianissimo"
        case .multilingual: return "Parakeet Ultra"
        }
    }

    var pitch: String {
        switch self {
        case .swedish: return String(localized: "Bäst på svenska – hänger med i engelska också.")
        case .multilingual: return String(localized: "25 språk, bland annat svenska, engelska, tyska och franska.")
        }
    }

    /// The model to suggest on this Mac: Swedish for anyone who speaks it — Swedish
    /// among the Mac's languages, or the region set to Sweden (plenty of Swedes run
    /// their Mac in English) — the multilingual one for everyone else. The app's own
    /// language still follows the Mac.
    static var recommended: SpeechModel {
        let swedishLanguage = Locale.preferredLanguages.contains { $0.hasPrefix("sv") }
        let inSweden = Locale.current.region?.identifier == "SE"
        return swedishLanguage || inSweden ? .swedish : .multilingual
    }

    var credit: String {
        switch self {
        case .swedish: return "Klang AI AB"
        case .multilingual: return "Moondream"
        }
    }

    var modelPage: URL {
        switch self {
        case .swedish: return URL(string: "https://huggingface.co/KlangAI/pianissimo-sv")!
        case .multilingual: return URL(string: "https://huggingface.co/moondream/parakeet-ultra")!
        }
    }

    /// The Core ML conversion we actually download.
    var conversionPage: URL { URL(string: "https://huggingface.co/\(repo)")! }

    var conversionCredit: String {
        switch self {
        case .swedish: return "markstrom"
        case .multilingual: return "FluidInference"
        }
    }

    // MARK: Pinned files

    struct RemoteFile: Sendable {
        let path: String
        let size: Int64
        let sha256: String
    }

    var repo: String {
        switch self {
        case .swedish: return "markstrom/pianissimo-sv-coreml"
        case .multilingual: return "FluidInference/parakeet-ultra-coreml"
        }
    }

    var revision: String {
        switch self {
        case .swedish: return "106fa163a138a0db6737e0c50494269e07f508d0"
        case .multilingual: return "95eaa59a39d4394f047a4dc5cce480388a60d1b6"
        }
    }

    var asrVersion: AsrModelVersion {
        switch self {
        case .swedish: return .v3
        case .multilingual: return .ultra
        }
    }

    /// `.mlpackage`s that are compiled on this Mac at install (the rest ships compiled).
    var packagesToCompile: [String] {
        switch self {
        case .swedish: return ["Preprocessor", "Encoder", "Decoder", "JointDecisionv3"]
        case .multilingual: return []
        }
    }

    var files: [RemoteFile] {
        switch self {
        case .swedish: return [
            .init(path: "Decoder.mlpackage/Data/com.apple.CoreML/model.mlmodel", size: 11811, sha256: "4a36039f091573251bd8bcb55e8f5fa6dc3bad32052d825b1e3b2a3840879990"),
            .init(path: "Decoder.mlpackage/Data/com.apple.CoreML/weights/weight.bin", size: 23604992, sha256: "9e34a5cc5da3477cf0e49126f55d4754a6a332b5df52a22812d6ddbd84af0e39"),
            .init(path: "Decoder.mlpackage/Manifest.json", size: 617, sha256: "99e8049015d297e58291cfe279c832f01e88959ca91894be939753b19f694827"),
            .init(path: "Encoder.mlpackage/Data/com.apple.CoreML/model.mlmodel", size: 677050, sha256: "9c9e8a95b18b35142f00ac3c8c186c669057f7ca4a097b2e463c99486eb8eeb8"),
            .init(path: "Encoder.mlpackage/Data/com.apple.CoreML/weights/weight.bin", size: 649181632, sha256: "e9623b969e8f31ba12bfbec7cdcdacb3bfb74e5a3a9bd3a812e4a942c1b1b9ed"),
            .init(path: "Encoder.mlpackage/Manifest.json", size: 617, sha256: "6d235c39782d2e839142f0df9a6fc3096a6258da05f8be1c1d95547d936cf619"),
            .init(path: "JointDecisionv3.mlpackage/Data/com.apple.CoreML/model.mlmodel", size: 10827, sha256: "af1b876c8677cc8c6676573801038ce6da4972eba4817b1caa2eb09b9ff5ed32"),
            .init(path: "JointDecisionv3.mlpackage/Data/com.apple.CoreML/weights/weight.bin", size: 12642764, sha256: "1f841daf3a6ce483ac136cd7a5e48d6071543e7c5eddcbba4525bda74695cb61"),
            .init(path: "JointDecisionv3.mlpackage/Manifest.json", size: 617, sha256: "42ab119e793f459e4a8d4a86f77714f093a54ebea55a14786981962985e8121d"),
            .init(path: "Preprocessor.mlpackage/Data/com.apple.CoreML/model.mlmodel", size: 17913, sha256: "44426745838b32d86e0de13054eea2b7c4439e95dadef0bcfa528bdfc93eb630"),
            .init(path: "Preprocessor.mlpackage/Data/com.apple.CoreML/weights/weight.bin", size: 1953088, sha256: "c69139820fc62c199f92c83d2c97458f8aaff337e5026abd9606ff03ba52b8e1"),
            .init(path: "Preprocessor.mlpackage/Manifest.json", size: 617, sha256: "caffd54a12af3df9bb673f100239919e1047ece8c4035f75819f4adc1d71b6a9"),
            .init(path: "parakeet_vocab.json", size: 151122, sha256: "7ec60e05f1b24480736ec0eed40900f4626bce1fa9a60fd700ec7e2a59198735"),
            .init(path: "LICENSE-and-attribution.txt", size: 2402, sha256: "cebcc87c23d9b4df78a1e1655b20c5f82e66babde45362a58fe6a2cb64f3dc91"),
        ]
        case .multilingual: return [
            .init(path: "Decoder.mlmodelc/analytics/coremldata.bin", size: 243, sha256: "fe92b6cfaa012abd5248c0bc877832f19807015abffc60d87b8ccc8ccb48b3b5"),
            .init(path: "Decoder.mlmodelc/coremldata.bin", size: 560, sha256: "3b06e66768f0df7e21795f50e2b29300e33eeb1a2579dc42c695279c2d308497"),
            .init(path: "Decoder.mlmodelc/model.mil", size: 13110, sha256: "956f600207f88396017ca5c96cfa3acd5bfee60835a5b44b762e033a8fb28955"),
            .init(path: "Decoder.mlmodelc/weights/weight.bin", size: 23604992, sha256: "02a0d219f281b9665bc10c8768649403b2eebbbf4b44c627041d948e0de11bb4"),
            .init(path: "Encoder.mlmodelc/analytics/coremldata.bin", size: 243, sha256: "d87101d824d6723cf95304da33755c3c60e762663b9ef2b0c4bd0aa166a09a0d"),
            .init(path: "Encoder.mlmodelc/coremldata.bin", size: 514, sha256: "397a84a4062f563cbc5f56077c674f09a61d85be5090f61d2f1932afb92ac0fe"),
            .init(path: "Encoder.mlmodelc/model.mil", size: 1002653, sha256: "f5d601568a4171d99a314c0fe3f6bc67715da2623732a3fb566e050ea83e5848"),
            .init(path: "Encoder.mlmodelc/weights/weight.bin", size: 594211328, sha256: "315ba01f33cadbf601d43ac7f5c86208b7aa75fdaa34705c9869d5abe3521c9b"),
            .init(path: "JointDecisionv3.mlmodelc/analytics/coremldata.bin", size: 243, sha256: "68d38ca646aebafa7a9329e2efda50f5767c49e89fdb5f77f212072bb66f97c4"),
            .init(path: "JointDecisionv3.mlmodelc/coremldata.bin", size: 592, sha256: "5e3af5a4ce686f6c237cadbd9284e10d333bc0e1546633cd0e430e6194044bc4"),
            .init(path: "JointDecisionv3.mlmodelc/model.mil", size: 11777, sha256: "791b3c3cf3eb2079c84623fc880f6bba008d1366e5f8b03e9a8ed8bd4d7194a0"),
            .init(path: "JointDecisionv3.mlmodelc/weights/weight.bin", size: 12642764, sha256: "3f310b85b82341c53ec383025ab094a4e462ee1c592e1ad7c6bfe39cff66ca25"),
            .init(path: "Preprocessor.mlmodelc/analytics/coremldata.bin", size: 243, sha256: "c9beeb989c8d66f8be11df59bc6df277ec76cee404f6865b46243835ef562f6d"),
            .init(path: "Preprocessor.mlmodelc/coremldata.bin", size: 486, sha256: "dbde3f2300842c1fd51ef3ff948a0bcffe65ffd2dca10707f2509f32c1d65b1d"),
            .init(path: "Preprocessor.mlmodelc/metadata.json", size: 2841, sha256: "2a98699e22d279dd37fa1d238aeb1c6db1df0d6fad687775324157689d8f3acf"),
            .init(path: "Preprocessor.mlmodelc/model.mil", size: 28181, sha256: "4b8518a956450fec57f06c2a21bdffc26973f7f1fa6842fb38fe917f896b6b93"),
            .init(path: "Preprocessor.mlmodelc/weights/weight.bin", size: 491072, sha256: "129b76e3aeafa8afa3ea76d995b964b145fe83700d579f6ff42c4c38fa0968ea"),
            .init(path: "parakeet_vocab.json", size: 151122, sha256: "7ec60e05f1b24480736ec0eed40900f4626bce1fa9a60fd700ec7e2a59198735"),
            .init(path: "README.md", size: 2235, sha256: "9e9e26b6da628a0dc34b9c55f2029e486fc02304ccd7f83825333572ae11c158"),
        ]
        }
    }

    var downloadBytes: Int64 { files.reduce(0) { $0 + $1.size } }

    /// "690 MB".
    /// "312 av 688 MB · ca 2 min kvar" — or "Förbereder…" once the files are in
    /// (the last tenth of `progress` is checking and compiling).
    func progressText(_ progress: Double, started: Date?) -> String {
        guard !Self.isPreparing(progress) else { return String(localized: "Kontrollerar och förbereder för din Mac…") }
        let total = Double(downloadBytes) / 1_000_000
        let done = min(1, progress / 0.9) * total
        var text = String(localized: "\(Int(done)) av \(Int(total)) MB")
        if let started, progress > 0.03 {
            let elapsed = Date().timeIntervalSince(started)
            let left = elapsed * (0.9 - progress) / progress
            if elapsed > 3, left >= 5 {
                text += " · " + (left < 60 ? String(localized: "ca \(Int((left / 10).rounded(.up) * 10)) s kvar")
                                          : String(localized: "ca \(Int((left / 60).rounded())) min kvar"))
            }
        }
        return text
    }

    /// All files in; now they're checked and compiled (`progress` 0.9 → 1).
    static func isPreparing(_ progress: Double) -> Bool { progress >= 0.899 }

    var sizeText: String { "\(Int((Double(downloadBytes) / 1_000_000).rounded())) MB" }

    // MARK: Locations

    private static var appSupport: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
    }

    /// Leftovers of a download that was interrupted by quitting (the cleanup in
    /// `install` never ran). Called at launch.
    static func removeLeftoverDownloads() {
        let fm = FileManager.default
        guard let items = try? fm.contentsOfDirectory(at: rootDirectory, includingPropertiesForKeys: nil) else { return }
        for item in items where item.lastPathComponent.hasPrefix(".staging-") { try? fm.removeItem(at: item) }
    }

    static var rootDirectory: URL {
        #if DEBUG
        // Tests of a first install: a separate, empty folder instead of yours.
        if let dir = Demo.modelsDirectory { return URL(fileURLWithPath: dir) }
        #endif
        return appSupport.appendingPathComponent("Mindtalk").appendingPathComponent("Models")
    }

    private var folderName: String {
        switch self {
        case .swedish: return "pianissimo-sv-\(revision.prefix(8))"
        case .multilingual: return "parakeet-ultra-\(revision.prefix(8))"
        }
    }

    /// Where Mindtalk installs it.
    var ownDirectory: URL { Self.rootDirectory.appendingPathComponent(folderName) }

    /// The install, once it's complete.
    var directory: URL? { isComplete(ownDirectory) ? ownDirectory : nil }
    var isInstalled: Bool {
        #if DEBUG
        if Demo.fresh { return false }   // screenshots of a first launch
        #endif
        return directory != nil
    }
    /// Installed by Mindtalk itself (so Mindtalk may remove it).
    var isOwnInstall: Bool { isComplete(ownDirectory) }

    private static let completeMarker = ".complete"

    private func isComplete(_ dir: URL) -> Bool {
        let fm = FileManager.default
        guard fm.fileExists(atPath: dir.appendingPathComponent(Self.completeMarker).path),
              fm.fileExists(atPath: dir.appendingPathComponent("parakeet_vocab.json").path) else { return false }
        return ["Preprocessor", "Encoder", "Decoder", "JointDecisionv3"]
            .allSatisfy { fm.fileExists(atPath: dir.appendingPathComponent("\($0).mlmodelc").path) }
    }

    // MARK: Install (download → verify → compile)

    /// `progress` is 0…1 (download ≈ 90 %, compile/move ≈ 10 %).
    func install(progress: @escaping @Sendable (Double) -> Void) async throws {
        #if DEBUG
        // Tests: the network drops a few seconds in.
        if CommandLine.arguments.contains("--fail-download") {
            for i in 1...12 { try await Task.sleep(for: .milliseconds(250)); progress(Double(i) * 0.01) }
            throw URLError(.networkConnectionLost)
        }
        #endif
        let fm = FileManager.default
        try fm.createDirectory(at: Self.rootDirectory, withIntermediateDirectories: true)
        let staging = Self.rootDirectory.appendingPathComponent(".staging-\(UUID().uuidString)")
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }

        let raw = staging.appendingPathComponent("raw")
        let out = staging.appendingPathComponent("ready")
        try fm.createDirectory(at: out, withIntermediateDirectories: true)

        var doneBytes: Int64 = 0
        let total = Double(downloadBytes)
        for file in files {
            try Task.checkCancellation()
            let dest = raw.appendingPathComponent(file.path)
            try fm.createDirectory(at: dest.deletingLastPathComponent(), withIntermediateDirectories: true)
            let base = doneBytes
            try await Self.download(repo: repo, revision: revision, file: file, to: dest) { written in
                progress(0.9 * Double(base + written) / total)
            }
            guard try Self.sha256(of: dest) == file.sha256 else {
                throw ModelError.checksumMismatch(file.path)
            }
            doneBytes += file.size
        }

        let packages = packagesToCompile
        for (i, name) in packages.enumerated() {
            try Task.checkCancellation()
            let pkg = raw.appendingPathComponent("\(name).mlpackage")
            let compiled = try await MLModel.compileModel(at: pkg)
            try fm.moveItem(at: compiled, to: out.appendingPathComponent("\(name).mlmodelc"))
            // Drop the source right away — keeps peak disk use near 1× the model.
            try? fm.removeItem(at: pkg)
            progress(0.9 + 0.1 * Double(i + 1) / Double(packages.count))
        }
        // Everything else (compiled models, vocabulary, license) goes in as is.
        for item in try fm.contentsOfDirectory(atPath: raw.path) {
            try fm.moveItem(at: raw.appendingPathComponent(item), to: out.appendingPathComponent(item))
        }
        try Data().write(to: out.appendingPathComponent(Self.completeMarker))

        if fm.fileExists(atPath: ownDirectory.path) { try fm.removeItem(at: ownDirectory) }
        try fm.moveItem(at: out, to: ownDirectory)
        progress(1)
    }

    /// Removes Mindtalk's own copy (never another app's).
    func uninstall() throws {
        if FileManager.default.fileExists(atPath: ownDirectory.path) {
            try FileManager.default.removeItem(at: ownDirectory)
        }
    }

    private static func download(repo: String, revision: String, file: RemoteFile, to dest: URL,
                                 progress: @escaping @Sendable (Int64) -> Void) async throws {
        let url = URL(string: "https://huggingface.co/\(repo)/resolve/\(revision)/\(file.path)")!
        let delegate = DownloadDelegate(destination: dest, expectedBytes: Int64(file.size), progress: progress)
        let session = URLSession(configuration: .default, delegate: delegate, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                delegate.continuation = cont
                session.downloadTask(with: url).resume()
            }
        } onCancel: {
            session.invalidateAndCancel()
        }
        if let code = delegate.statusCode, !(200..<300).contains(code) {
            throw ModelError.badResponse(file.path, code)
        }
    }

    /// Streams the file through SHA-256 so a 650 MB encoder never sits in memory.
    static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 8 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}

enum ModelError: LocalizedError {
    case notInstalled
    case checksumMismatch(String)
    case badResponse(String, Int)

    var errorDescription: String? {
        switch self {
        case .notInstalled: return String(localized: "Språkmodellen är inte nedladdad.")
        case .checksumMismatch(let f): return String(localized: "Nedladdningen av \(f) blev skadad. Försök igen.")
        case .badResponse(let f, let code): return String(localized: "Kunde inte ladda ned \(f) (HTTP \(code)).")
        }
    }
}

// MARK: - Runtime

/// Holds one model in memory at a time — switching language unloads the other.
actor SpeechEngine {
    static let shared = SpeechEngine()

    private var loaded: (model: SpeechModel, asr: AsrManager)?
    private var loading: (model: SpeechModel, task: Task<AsrManager, Error>)?
    /// Transcriptions running right now — a model is never unloaded under one.
    private var inFlight = 0

    func loadedModel() -> SpeechModel? { loaded?.model }

    /// Loads `model` (unloading any other) and runs one silent pass, so Core ML has
    /// built its Neural Engine plan before the first real dictation.
    func load(_ model: SpeechModel) async throws {
        _ = try await manager(for: model)
    }

    private func manager(for model: SpeechModel) async throws -> AsrManager {
        if let loaded, loaded.model == model { return loaded.asr }
        if let loading, loading.model == model { return try await loading.task.value }
        guard let dir = model.directory else { throw ModelError.notInstalled }
        if let old = loaded {
            while inFlight > 0 { try await Task.sleep(for: .milliseconds(50)) }
            await old.asr.cleanup()
            loaded = nil
        }
        let version = model.asrVersion
        let task = Task { () async throws -> AsrManager in
            let models = try AsrModels.loadLocal(from: dir, version: version)
            let asr = AsrManager(config: .default)
            try await asr.loadModels(models)
            var warm = TdtDecoderState.make(decoderLayers: 2)
            _ = try? await asr.transcribe([Float](repeating: 0, count: 16_000), decoderState: &warm)
            return asr
        }
        loading = (model, task)
        defer { if loading?.model == model { loading = nil } }
        let asr = try await task.value
        loaded = (model, asr)
        return asr
    }

    func unload(_ model: SpeechModel) async {
        while inFlight > 0 { try? await Task.sleep(for: .milliseconds(50)) }
        guard let current = loaded, current.model == model else { return }
        await current.asr.cleanup()
        loaded = nil
    }

    /// Transcribes 16 kHz mono samples. Short clips are padded with silence —
    /// the model needs at least a second of audio.
    func transcribe(samples: [Float], with model: SpeechModel) async throws -> String {
        let asr = try await manager(for: model)
        inFlight += 1
        defer { inFlight -= 1 }
        let pad = [Float](repeating: 0, count: 4_800)                 // 0.3 s each side
        var audio = pad + samples + pad
        if audio.count < 24_000 { audio += [Float](repeating: 0, count: 24_000 - audio.count) }
        var state = TdtDecoderState.make(decoderLayers: 2)
        let result = try await asr.transcribe(audio, decoderState: &state)
        return result.text.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

/// Per-download URLSession delegate: reports bytes written and moves the finished
/// file into place before URLSession deletes its temporary copy.
private final class DownloadDelegate: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    let destination: URL
    let progress: @Sendable (Int64) -> Void
    var continuation: CheckedContinuation<Void, Error>?
    var statusCode: Int?
    private var moveError: Error?

    let expectedBytes: Int64
    private var oversized = false

    init(destination: URL, expectedBytes: Int64, progress: @escaping @Sendable (Int64) -> Void) {
        self.destination = destination
        self.expectedBytes = expectedBytes
        self.progress = progress
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask,
                    didWriteData bytesWritten: Int64, totalBytesWritten: Int64,
                    totalBytesExpectedToWrite: Int64) {
        // Never more than the pinned file's size: a misbehaving server can't fill the disk.
        if totalBytesWritten > expectedBytes + 1_000_000 { oversized = true; downloadTask.cancel(); return }
        progress(totalBytesWritten)
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask,
                    didFinishDownloadingTo location: URL) {
        statusCode = (downloadTask.response as? HTTPURLResponse)?.statusCode
        do {
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: location, to: destination)
        } catch {
            moveError = error
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if oversized {
            continuation?.resume(throwing: URLError(.dataLengthExceedsMaximum))
        } else if let error = error ?? moveError {
            continuation?.resume(throwing: error)
        } else {
            continuation?.resume()
        }
        continuation = nil
    }
}
