import SwiftUI

/// Three headline numbers and the last seven days, on the Diktering page.
struct StatsCard: View {
    @ObservedObject var stats: Stats

    var body: some View {
        let today = stats.today
        let weekSaved = stats.week.reduce(0) { $0 + $1.stat.savedSeconds }
        VStack(alignment: .leading, spacing: 22) {
            HStack(alignment: .top, spacing: 0) {
                tile("I dag", count: today.words, unit: "ord",
                     caption: today.dictations == 1 ? String(localized: "1 diktering") : String(localized: "\(today.dictations) dikteringar"))
                tileDivider
                tile("Den här veckan", count: stats.weekWords, unit: "ord",
                     caption: weekSaved >= 1 ? String(localized: "≈ \(Stats.duration(weekSaved)) sparad") : String(localized: "Inget än"))
                    .help("Sparad tid jämfört med att skriva samma ord på tangentbordet i \(Int(Stats.typingWordsPerMinute)) ord per minut.")
                tileDivider
                tile("Totalt", count: stats.totalWords, unit: "ord",
                     caption: stats.totalWords == 0 ? String(localized: "Inget än") : stats.streak > 1 ? String(localized: "\(stats.streak) dagar i rad") : String(localized: "≈ \(Stats.duration(stats.totalSaved)) sparad totalt"))
            }
            VStack(alignment: .leading, spacing: 10) {
                HStack(alignment: .firstTextBaseline) {
                    Text("Vecka \(stats.weekNumber)").font(.system(size: 12, weight: .semibold)).foregroundStyle(DS.Colors.muted)
                    Spacer()
                }
                WeekChart(week: stats.week)
                    .frame(height: 118)
            }
        }
        .padding(22)
        .card()
    }

    private var tileDivider: some View {
        Rectangle().fill(DS.Colors.divider).frame(width: 1, height: 58).padding(.horizontal, 20)
    }

    /// A tile whose number counts up when the page appears.
    private func tile(_ title: String, count: Int, unit: String?, caption: String) -> some View {
        tile(title, number: CountUp(target: count), unit: unit, caption: caption)
    }

    private func tile(_ title: String, value: String, unit: String?, caption: String) -> some View {
        tile(title, number: Text(value).contentTransition(.numericText()), unit: unit, caption: caption)
    }

    private func tile<Number: View>(_ title: String, number: Number, unit: String?, caption: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(LocalizedStringKey(title)).font(.system(size: 12, weight: .semibold)).foregroundStyle(DS.Colors.muted)
            HStack(alignment: .firstTextBaseline, spacing: 5) {
                number
                    .font(.system(size: 28, weight: .regular, design: .serif))
                    .monospacedDigit()
                if let unit { Text(LocalizedStringKey(unit)).font(.system(size: 13)).foregroundStyle(DS.Colors.muted) }
            }
            Text(LocalizedStringKey(caption)).font(.system(size: 12)).foregroundStyle(DS.Colors.muted).lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }
}

/// Words per day this week, Monday to Sunday: one hue, today at full strength,
/// days still to come as faint placeholders. Values show on today's bar and on hover.
private struct WeekChart: View {
    let week: [Stats.WeekDay]
    @State private var hovered: Int?
    @State private var grown = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private static let weekday: DateFormatter = {
        let f = DateFormatter()
        f.locale = AppLanguage.locale
        f.dateFormat = "EEE"
        return f
    }()

    private static let fullDay: DateFormatter = {
        let f = DateFormatter()
        f.locale = AppLanguage.locale
        f.dateFormat = "EEEE d MMMM"
        return f
    }()

    /// "måndag 21 september" → "Måndag 21 september" (only the first letter).
    private static func sentenceCase(_ s: String) -> String {
        s.prefix(1).uppercased() + s.dropFirst()
    }

    var body: some View {
        let peak = max(1, week.map(\.stat.words).max() ?? 1)
        let empty = week.allSatisfy { $0.stat.words == 0 }
        VStack(spacing: 6) {
            HStack(alignment: .bottom, spacing: 10) {
                ForEach(week.indices, id: \.self) { i in
                    let day = week[i]
                    let words = day.stat.words
                    let showValue = words > 0 && (day.isToday || hovered == i)
                    VStack(spacing: 4) {
                        Spacer(minLength: 0)
                        Text(Stats.number(words))
                            .font(.system(size: 11, weight: .semibold))
                            .monospacedDigit()
                            .foregroundStyle(DS.Colors.muted)
                            .opacity(showValue ? 1 : 0)
                        UnevenRoundedRectangle(topLeadingRadius: 4, topTrailingRadius: 4, style: .continuous)
                            .fill(barColor(day, hovered: hovered == i))
                            .frame(height: words == 0 ? 2 : max(4, CGFloat(words) / CGFloat(peak) * 72))
                            // Bars grow up from the baseline, one after another.
                            .scaleEffect(x: 1, y: grown || reduceMotion ? 1 : 0.02, anchor: .bottom)
                            .animation(.spring(duration: 0.7, bounce: 0.25).delay(0.25 + Double(i) * 0.06), value: grown)
                    }
                    .frame(maxWidth: .infinity)
                    .contentShape(Rectangle())
                    .onHover { hovered = $0 ? i : (hovered == i ? nil : hovered) }
                    .help(day.isFuture ? "" : String(localized: "\(Self.sentenceCase(Self.fullDay.string(from: day.date))): \(Stats.number(words)) ord"))
                    .accessibilityElement()
                    .accessibilityLabel(day.isFuture ? String(localized: "\(Self.fullDay.string(from: day.date)): inte än") : String(localized: "\(Self.fullDay.string(from: day.date)): \(words) ord"))
                }
            }
            Rectangle().fill(DS.Colors.divider).frame(height: 1)
            HStack(spacing: 10) {
                ForEach(week.indices, id: \.self) { i in
                    let day = week[i]
                    Text(Self.weekday.string(from: day.date).capitalized)
                        .font(.system(size: 11, weight: day.isToday ? .bold : .regular))
                        .foregroundStyle(day.isToday ? AnyShapeStyle(.primary)
                                         : day.isFuture ? AnyShapeStyle(DS.Colors.faint) : AnyShapeStyle(DS.Colors.muted))
                        .frame(maxWidth: .infinity)
                        .overlay(alignment: .bottom) {
                            if day.isToday {
                                Circle().fill(DS.Colors.accent).frame(width: 4, height: 4).offset(y: 7)
                            }
                        }
                }
            }
            .padding(.bottom, 6)
        }
        .onAppear { grown = true }
        .overlay {
            if empty {
                Text("Din vecka syns här när du börjar diktera.")
                    .font(.system(size: 12))
                    .foregroundStyle(DS.Colors.muted)
                    .offset(y: -18)
            }
        }
    }

    private func barColor(_ day: Stats.WeekDay, hovered: Bool) -> Color {
        // Days without words are a quiet baseline, not a bright stub.
        if day.isFuture || day.stat.words == 0 { return DS.Colors.divider }
        return DS.Colors.accent.opacity(day.isToday || hovered ? 1 : 0.4)
    }
}
