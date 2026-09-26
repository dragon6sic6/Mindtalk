#if DEBUG
import Foundation

// MARK: - Demo mode (debug builds only)
//
// For the screenshots in the README: `--demo` fills Senaste and the statistics
// with example data held in memory — nothing is read from or written to your
// own history, stats or settings — and treats the app as ready.
//
//   Mindtalk --demo [--show-panel] [--demo-hud] [--onboarding-step key]
//   (add `-appearance dark` for dark mode; argument defaults are never saved)

@MainActor
enum Demo {
    nonisolated static let on = CommandLine.arguments.contains("--demo")
    nonisolated static let hud = CommandLine.arguments.contains("--demo-hud")
    /// As on a first launch: no language model installed yet.
    nonisolated static let fresh = CommandLine.arguments.contains("--fresh")

    /// `--onboarding-step welcome|language|permissions|key|tryIt`
    nonisolated static var onboardingStep: String? {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--onboarding-step"), i + 1 < args.count else { return nil }
        return args[i + 1]
    }

    static var recent: [Dictation.Entry] {
        let now = Date()
        let english = Bundle.main.preferredLocalizations.first == "en"
        let texts: [(String, Double)] = english ? [
            ("Thanks for today! I'll send over the proposal tomorrow morning and we'll talk on Thursday.", 4),
            ("Can we move the meeting to three o'clock? I'm stuck on a call until then.", 38),
            ("Summary: the customer wants a pilot in January, the budget is approved and the next step is a contract.", 95),
            ("Tack för i dag, vi ses på måndag!", 180),
            ("Remember to pick up milk, bread and coffee on the way home.", 60 * 26),
        ] : [
            ("Tack för i dag! Jag skickar över offerten i morgon förmiddag, så hörs vi på torsdag.", 4),
            ("Kan vi flytta mötet till klockan tre? Jag sitter fast i ett samtal fram till dess.", 38),
            ("Sammanfattning: kunden vill ha en pilot i januari, budgeten är godkänd och nästa steg är ett avtal.", 95),
            ("Let's ship the new onboarding on Monday and collect feedback during the week.", 180),
            ("Kom ihåg att köpa mjölk, bröd och kaffe på vägen hem.", 60 * 26),
        ]
        return texts.map { Dictation.Entry(text: $0.0, date: now.addingTimeInterval(-$0.1 * 60)) }
    }

    /// A believable week: busier midweek, today under way.
    static var days: [String: DayStat] {
        var calendar = Calendar(identifier: .iso8601)
        calendar.firstWeekday = 2
        let today = calendar.startOfDay(for: Date())
        let weekday = (calendar.component(.weekday, from: today) + 5) % 7   // Monday = 0
        let words = [1_240, 1_860, 2_310, 1_690, 980, 420, 310]
        var days: [String: DayStat] = [:]
        // This week up to today, and a few weeks before, for the totals and the streak.
        for offset in 0...(weekday + 21) {
            guard let date = calendar.date(byAdding: .day, value: -offset, to: today) else { continue }
            let index = (calendar.component(.weekday, from: date) + 5) % 7
            let w = offset == 0 ? 640 : words[index]
            days[Stats.key(for: date)] = DayStat(words: w, seconds: Double(w) / 150 * 60, dictations: max(1, w / 45))
        }
        return days
    }
}
#endif
