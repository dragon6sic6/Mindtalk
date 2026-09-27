import Foundation

// MARK: - The AI polish guard
//
// The on-device model may tidy a dictation, never change what was said. Its
// answer is accepted only if, word by word, it can be read back onto the
// original with nothing but:
//   • words kept (any case),
//   • words split or joined ("köpmjöl" → "köp mjöl"),
//   • deletions of hesitation sounds, words said twice, spoken punctuation
//     ("punkt", "comma"), or what a self-correction replaces ("tisdag, nej jag
//     menar onsdag" → "onsdag").
// Nothing may be added, and a negation (inte, aldrig, not, don't …) is never
// dropped — "jag godkänner inte" can't become "jag godkänner". Only ordinary
// punctuation may appear; control and bidi characters are refused.
// Self-contained, so it can be tested on its own.

enum PolishGuard {
    static func accepts(_ polished: String, as original: String) -> Bool {
        guard onlyOrdinaryCharacters(polished), !gluesWords(polished, original) else { return false }
        let before = words(original), after = words(polished)
        guard !after.isEmpty, Double(after.count) >= Double(before.count) * 0.5 else { return false }

        var i = 0, j = 0
        while j < after.count {
            if i < before.count, let step = match(before, i, after, j) {
                i += step.before; j += step.after
                continue
            }
            // Not the next original word: the words up to its next occurrence were dropped.
            guard let k = (i..<min(before.count, i + 9)).first(where: { match(before, $0, after, j) != nil }),
                  deletable(before, i..<k) else { return false }
            i = k
        }
        return i == before.count || deletable(before, i..<before.count)
    }

    // MARK: Words

    static func words(_ text: String) -> [String] {
        let normalized = text.lowercased().replacingOccurrences(of: "’", with: "'")
        let pattern = #"[\p{L}\p{N}]+(?:'[\p{L}\p{N}]+)*"#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return [] }
        let ns = normalized as NSString
        return regex.matches(in: normalized, range: NSRange(location: 0, length: ns.length)).map { ns.substring(with: $0.range) }
    }

    /// The same word, or one split into two/three, or two/three joined into one.
    private static func match(_ before: [String], _ i: Int, _ after: [String], _ j: Int) -> (before: Int, after: Int)? {
        if before[i] == after[j] { return (1, 1) }
        for n in 2...3 {
            if j + n <= after.count, after[j..<j + n].joined() == before[i] { return (1, n) }
            if i + n <= before.count, before[i..<i + n].joined() == after[j] { return (n, 1) }
        }
        return nil
    }

    // MARK: What may be dropped

    private static let fillers: Set<String> = ["eh", "ehm", "eeh", "öh", "öhm", "äh", "ähm", "um", "umm", "uh", "uhm",
                                               "hm", "hmm", "mm", "erm"]
    private static let spokenPunctuation: Set<String> = ["punkt", "komma", "frågetecken", "utropstecken", "kolon",
                                                         "semikolon", "tankstreck", "period", "comma", "colon",
                                                         "semicolon", "question", "exclamation", "mark", "point",
                                                         "dash", "full", "stop"]
    /// Never dropped: they flip the meaning.
    private static let negations: Set<String> = ["inte", "ej", "icke", "aldrig", "ingen", "inget", "inga", "ingenting",
                                                 "not", "never", "nothing", "nobody", "none", "neither", "nor", "cannot"]
    /// "nej"/"no" may go only as part of a correction ("nej jag menar").
    private static let softNo: Set<String> = ["nej", "no", "nä", "nope"]
    private static let correctionCues: [[String]] = [["jag", "menar"], ["menar", "jag"], ["förlåt"], ["ursäkta"],
                                                     ["rättelse"], ["i", "mean"], ["sorry"], ["correction"],
                                                     ["or", "rather"], ["eller", "snarare"]]

    private static func deletable(_ before: [String], _ run: Range<Int>) -> Bool {
        guard !run.isEmpty else { return true }
        let dropped = Array(before[run])
        if dropped.contains(where: { negations.contains($0) || $0.hasSuffix("n't") }) { return false }
        // A self-correction: the replaced words and the cue go together (at most eight words).
        if dropped.count <= 8, correctionCues.contains(where: { cue in contains(dropped, cue) }) { return true }
        return run.allSatisfy { idx in
            let w = before[idx]
            if fillers.contains(w) || spokenPunctuation.contains(w) { return true }
            // Said twice in a row.
            return (idx > 0 && before[idx - 1] == w) || (idx + 1 < before.count && before[idx + 1] == w)
        }
    }

    private static func contains(_ words: [String], _ cue: [String]) -> Bool {
        guard words.count >= cue.count else { return false }
        return (0...(words.count - cue.count)).contains { Array(words[$0..<$0 + cue.count]) == cue }
    }

    /// Punctuation squeezed between two letters ("mind.act") that wasn't in the original.
    private static func gluesWords(_ polished: String, _ original: String) -> Bool {
        guard let regex = try? NSRegularExpression(pattern: #"\p{L}[.,;:!?/&+@#*]\p{L}"#) else { return false }
        func count(_ s: String) -> Int { regex.numberOfMatches(in: s, range: NSRange(location: 0, length: (s as NSString).length)) }
        return count(polished) > count(original)
    }

    // MARK: Characters

    private static let allowedPunctuation = CharacterSet(charactersIn: ".,;:!?'\"’‘“”«»()-–—…%/&+@#*")

    private static func onlyOrdinaryCharacters(_ text: String) -> Bool {
        text.unicodeScalars.allSatisfy { c in
            if c == "\n" || c == " " { return true }
            if CharacterSet.letters.contains(c) || CharacterSet.decimalDigits.contains(c) { return true }
            if CharacterSet.nonBaseCharacters.contains(c) { return true }      // accents
            return allowedPunctuation.contains(c)
        }
    }
}
