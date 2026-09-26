import Foundation

// MARK: - Ordlista
//
// Names and words Mindtalk should spell your way. Each entry is fixed in the
// text after transcription, in two predictable ways:
//   1. The word itself, however it got split or cased: "Mind Talk", "mind-talk",
//      "mindtalk" → "Mindtalk" (a possessive -s is kept: "Mindtalks").
//   2. What it tends to come out as, which you add yourself: "min dag" → "Mindact".
//      Only yours — "min dag" is also ordinary Swedish, so Mindtalk never guesses it.

struct VocabularyEntry: Codable, Identifiable, Equatable {
    var id = UUID()
    var word: String
    var heardAs: [String] = []
    /// How many times it has corrected something.
    var fixes = 0
}

@MainActor
final class Vocabulary: ObservableObject {
    static let shared = Vocabulary()

    @Published private(set) var entries: [VocabularyEntry] {
        didSet { save() }
    }

    private static let key = "vocabulary"

    private init() {
        if let data = UserDefaults.standard.data(forKey: Self.key),
           let saved = try? JSONDecoder().decode([VocabularyEntry].self, from: data) {
            entries = saved
        } else {
            entries = [VocabularyEntry(word: "Mindtalk"), VocabularyEntry(word: "Mindact")]
        }
    }

    private func save() {
        UserDefaults.standard.set(try? JSONEncoder().encode(entries), forKey: Self.key)
    }

    /// Adds a word (or more "heard as" variants to an existing one). `heardAs` is comma-separated.
    func add(word: String, heardAs: String) {
        let word = word.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !word.isEmpty else { return }
        let variants = heardAs.split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty && $0.caseInsensitiveCompare(word) != .orderedSame }
        if let i = entries.firstIndex(where: { $0.word.caseInsensitiveCompare(word) == .orderedSame }) {
            entries[i].word = word
            for v in variants where !entries[i].heardAs.contains(where: { $0.caseInsensitiveCompare(v) == .orderedSame }) {
                entries[i].heardAs.append(v)
            }
        } else {
            entries.insert(VocabularyEntry(word: word, heardAs: variants), at: 0)
        }
    }

    func remove(_ entry: VocabularyEntry) {
        entries.removeAll { $0.id == entry.id }
    }

    func removeVariant(_ variant: String, from entry: VocabularyEntry) {
        guard let i = entries.firstIndex(where: { $0.id == entry.id }) else { return }
        entries[i].heardAs.removeAll { $0 == variant }
    }

    /// Fixes the text again without counting — after the polish, which may
    /// have undone a spelling.
    func reapply(_ text: String) -> String {
        entries.reduce(text) { Self.apply($1, to: $0).0 }
    }

    /// Fixes the text and counts what it fixed.
    func apply(_ text: String) -> String {
        var result = text
        var updated = entries
        for i in updated.indices {
            let (fixed, count) = Self.apply(updated[i], to: result)
            if count > 0 {
                result = fixed
                updated[i].fixes += count
            }
        }
        if updated != entries { entries = updated }
        return result
    }

    /// Pure version, for one entry: the corrected text and how many spans changed.
    nonisolated static func apply(_ entry: VocabularyEntry, to text: String) -> (String, Int) {
        var text = text
        var count = 0
        var patterns = entry.heardAs.map { variant in
            // Any run of spaces between the variant's words.
            variant.split(whereSeparator: \.isWhitespace)
                .map { NSRegularExpression.escapedPattern(for: String($0)) }
                .joined(separator: "\\s+")
        }
        // The word itself, split by spaces or hyphens anywhere, any case.
        let letters = entry.word.filter { !$0.isWhitespace && $0 != "-" }
        if letters.count >= 3 {
            patterns.append(letters.map { NSRegularExpression.escapedPattern(for: String($0)) }
                .joined(separator: "[\\s-]?"))
        }
        for pattern in patterns where !pattern.isEmpty {
            guard let regex = try? NSRegularExpression(
                pattern: "(?<![\\p{L}\\p{N}])(?:\(pattern))(s?)(?![\\p{L}\\p{N}])",
                options: [.caseInsensitive]) else { continue }
            let range = NSRange(text.startIndex..., in: text)
            var changed = 0
            let replaced = NSMutableString(string: text)
            for match in regex.matches(in: text, range: range).reversed() {
                let original = (text as NSString).substring(with: match.range)
                let suffix = (text as NSString).substring(with: match.range(at: 1))
                let fixed = entry.word + suffix
                if original != fixed {
                    replaced.replaceCharacters(in: match.range, with: fixed)
                    changed += 1
                }
            }
            if changed > 0 {
                text = replaced as String
                count += changed
            }
        }
        return (text, count)
    }
}
