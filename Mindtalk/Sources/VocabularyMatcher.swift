import Foundation

// MARK: - Vocabulary matching
//
// Finds where the text should be spelled your way, for all entries at once,
// in one pass over the original text — so one entry can never rewrite another's
// result ("Bert" heard as "bart" and "Bart" heard as "bert" both work).
//
// Per entry:
//   • Variants you added ("min dag" → Mindact): always fixed, any case.
//   • The word itself, cased or joined differently ("mindtalk", "Mind-talk"),
//     or — for words of six letters or more — split once into parts of at least
//     three letters ("mind talk"). Short names are never pieced together from
//     bits: "an na" stays, "två gånger per dag" stays.
//   • Endings: -s, -en, -et, -ens, -ets ("Mindtalks", "Mindtalken").
// An unsplit match that is an ordinary dictionary word ("per", "klass", "test")
// is left alone, as is text in capitals ("MINDTALK"). A lowercase entry keeps a
// sentence's capital ("Mejl skickat."). Nothing matches across a line break.
// Self-contained (no AppKit), so it can be tested on its own; the dictionary
// check is passed in.

struct VocabularyMatcher {
    struct Fix: Equatable {
        let range: NSRange          // in the original text
        let replacement: String
        let entry: Int
    }

    private struct Rule {
        let entry: Int
        let regex: NSRegularExpression
        let isVariant: Bool
    }

    private let words: [String]
    private let rules: [Rule]

    static let endings = "(s|en|et|ens|ets)?"

    init(entries: [(word: String, heardAs: [String])]) {
        words = entries.map(\.word)
        var rules: [Rule] = []
        for (i, entry) in entries.enumerated() {
            for variant in entry.heardAs {
                let parts = variant.split(whereSeparator: \.isWhitespace).map { NSRegularExpression.escapedPattern(for: String($0)) }
                guard !parts.isEmpty, let regex = Self.regex(parts.joined(separator: "[ \\t]+"), endings: "(s)?") else { continue }
                rules.append(Rule(entry: i, regex: regex, isVariant: true))
            }
            // The word as written, its own spaces/hyphens optional…
            let pieces = entry.word.split(whereSeparator: { $0 == " " || $0 == "-" }).map(String.init)
            var forms = [pieces.map { NSRegularExpression.escapedPattern(for: $0) }.joined(separator: "[ \\t-]?")]
            //… and a single word of six letters or more split once, parts of three or more.
            if pieces.count == 1, pieces[0].count >= 6 {
                let letters = Array(pieces[0])
                for cut in 3...(letters.count - 3) {
                    forms.append(NSRegularExpression.escapedPattern(for: String(letters[..<cut])) + "[ \\t-]"
                                 + NSRegularExpression.escapedPattern(for: String(letters[cut...])))
                }
            }
            let letterCount = entry.word.filter { $0.isLetter || $0.isNumber }.count
            if letterCount >= 3, let regex = Self.regex(forms.joined(separator: "|"), endings: Self.endings) {
                rules.append(Rule(entry: i, regex: regex, isVariant: false))
            }
        }
        self.rules = rules
    }

    private static func regex(_ core: String, endings: String) -> NSRegularExpression? {
        try? NSRegularExpression(pattern: "(?<![\\p{L}\\p{N}])(?:\(core))\(endings)(?![\\p{L}\\p{N}])",
                                 options: [.caseInsensitive])
    }

    /// Every fix for `text`, non-overlapping, in order.
    func fixes(in text: String, isWord: (String) -> Bool) -> [Fix] {
        let ns = text as NSString
        let full = NSRange(location: 0, length: ns.length)
        var candidates: [(fix: Fix, priority: Int)] = []
        for (order, rule) in rules.enumerated() {
            for match in rule.regex.matches(in: text, range: full) {
                let matched = ns.substring(with: match.range)
                let ending = match.range(at: 1).location == NSNotFound ? "" : ns.substring(with: match.range(at: 1)).lowercased()
                let word = words[rule.entry]
                var replacement = word + ending
                if !rule.isVariant {
                    let split = matched.contains(where: { $0 == " " || $0 == "\t" || $0 == "-" })
                        && !word.contains(where: { $0 == " " || $0 == "-" })
                    let letters = matched.filter(\.isLetter)
                    // In capitals on purpose ("MINDTALK").
                    if letters.count > 1, letters == letters.uppercased(), word != word.uppercased() { continue }
                    // An ordinary word that happens to look like the entry ("per", "klass").
                    if !split, isWord(matched.lowercased()) { continue }
                }
                // A lowercase entry at the start of a sentence keeps the capital.
                if let first = word.first, first.isLowercase, let m = matched.first, m.isUppercase {
                    replacement = replacement.prefix(1).uppercased() + replacement.dropFirst()
                }
                guard replacement != matched else { continue }
                candidates.append((Fix(range: match.range, replacement: replacement, entry: rule.entry),
                                   (rule.isVariant ? 0 : 1_000_000) + order))
            }
        }
        // Earliest first; for the same start, the longest; then variants before words, then list order.
        candidates.sort { a, b in
            if a.fix.range.location != b.fix.range.location { return a.fix.range.location < b.fix.range.location }
            if a.fix.range.length != b.fix.range.length { return a.fix.range.length > b.fix.range.length }
            return a.priority < b.priority
        }
        var chosen: [Fix] = []
        var end = 0
        for c in candidates where c.fix.range.location >= end {
            chosen.append(c.fix)
            end = NSMaxRange(c.fix.range)
        }
        return chosen
    }

    /// The fixed text, how often each entry fixed something, and where the fixes
    /// ended up in the new text (for highlighting).
    func apply(to text: String, isWord: (String) -> Bool) -> (text: String, counts: [Int: Int], ranges: [NSRange]) {
        let fixes = fixes(in: text, isWord: isWord)
        guard !fixes.isEmpty else { return (text, [:], []) }
        let out = NSMutableString(string: text)
        var counts: [Int: Int] = [:]
        var shift = 0
        var ranges: [NSRange] = []
        for fix in fixes {
            let location = fix.range.location + shift
            out.replaceCharacters(in: NSRange(location: location, length: fix.range.length), with: fix.replacement)
            let length = (fix.replacement as NSString).length
            ranges.append(NSRange(location: location, length: length))
            shift += length - fix.range.length
            counts[fix.entry, default: 0] += 1
        }
        return (out as String, counts, ranges)
    }
}
