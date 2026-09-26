import SwiftUI

// MARK: - Ordlista page

struct VocabularyPage: View {
    @ObservedObject var vocabulary: Vocabulary
    @State private var word = ""
    @State private var heardAs = ""
    @State private var sample = ""
    @FocusState private var wordFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            PageHeader(title: "Ordlista",
                       subtitle: "Namn och ord som Mindtalk ska stava som du vill. Delade eller felskrivna varianter – som ”Mind Talk” – rättas automatiskt.")
                .padding(.bottom, 12)
                .staggered(0)

            SectionTitle("Lägg till")
            .staggered(0)
            HStack(alignment: .bottom, spacing: 12) {
                field("Ord", placeholder: "t.ex. Mindact", text: $word)
                    .focused($wordFocused)
                field("Blir ofta (valfritt)", placeholder: "t.ex. min dag, mind act", text: $heardAs)
                Button("Lägg till", action: add)
                    .buttonStyle(.ink)
                    .disabled(word.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            .padding(20)
            .card()
            .staggered(0)

            if !vocabulary.entries.isEmpty {
                SectionTitle("Dina ord")
                VStack(spacing: 0) {
                    ForEach(Array(vocabulary.entries.enumerated()), id: \.element.id) { i, entry in
                        if i > 0 { CardDivider() }
                        VocabularyRow(entry: entry, vocabulary: vocabulary)
                            .transition(.asymmetric(insertion: .move(edge: .top).combined(with: .opacity),
                                                    removal: .opacity))
                    }
                }
                .card()
            }

            SectionTitle("Prova ordlistan")
            .staggered(1)
            VStack(alignment: .leading, spacing: 12) {
                TextField("Skriv en mening som Mindtalk kunde ha hört, t.ex. ”jag jobbar på mind act”", text: $sample)
                    .textFieldStyle(.plain)
                    .font(.system(size: 14))
                    .padding(.horizontal, 12)
                    .padding(.vertical, 9)
                    .background(RoundedRectangle(cornerRadius: DS.Radius.control, style: .continuous).fill(DS.Colors.field))
                    .overlay(RoundedRectangle(cornerRadius: DS.Radius.control, style: .continuous)
                        .strokeBorder(DS.Colors.fieldStroke, lineWidth: 1))
                if !sample.isEmpty {
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Image(systemName: "arrow.turn.down.right").foregroundStyle(DS.Colors.muted)
                        Text(preview).font(.system(size: 14))
                    }
                }
            }
            .padding(20)
            .card()
            .staggered(1)

            Text("Varianter du lägger till rättas alltid – lägg bara till sådant du inte också säger på riktigt.")
                .font(.system(size: 12))
                .foregroundStyle(DS.Colors.muted)
                .padding(.top, 4)
        }
        .pageLayout()
    }

    private func field(_ label: String, placeholder: String, text: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(LocalizedStringKey(label)).font(.system(size: 12, weight: .semibold)).foregroundStyle(DS.Colors.muted)
            TextField(LocalizedStringKey(placeholder), text: text)
                .textFieldStyle(.plain)
                .font(.system(size: 14))
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(RoundedRectangle(cornerRadius: DS.Radius.control, style: .continuous).fill(DS.Colors.field))
                .overlay(RoundedRectangle(cornerRadius: DS.Radius.control, style: .continuous)
                    .strokeBorder(DS.Colors.fieldStroke, lineWidth: 1))
                .onSubmit(add)
        }
    }

    private func add() {
        guard !word.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        withAnimation(.spring(duration: 0.45, bounce: 0.2)) { vocabulary.add(word: word, heardAs: heardAs) }
        word = ""
        heardAs = ""
        wordFocused = true
    }

    /// The sample with every fixed word in the accent color.
    private var preview: AttributedString {
        var text = sample
        for entry in vocabulary.entries { text = Vocabulary.apply(entry, to: text).0 }
        var attributed = AttributedString(text)
        for entry in vocabulary.entries {
            var searchStart = attributed.startIndex
            while let range = attributed[searchStart...].range(of: entry.word) {
                attributed[range].foregroundColor = DS.Colors.accent
                attributed[range].font = .system(size: 14, weight: .semibold)
                searchStart = range.upperBound
            }
        }
        return attributed
    }
}

private struct VocabularyRow: View {
    let entry: VocabularyEntry
    @ObservedObject var vocabulary: Vocabulary
    @State private var hovering = false

    var body: some View {
        HStack(alignment: .center, spacing: 14) {
            Text(entry.word).font(.system(size: 15, weight: .semibold))
            if !entry.heardAs.isEmpty {
                HStack(spacing: 6) {
                    Text("blir ofta").font(.system(size: 12)).foregroundStyle(DS.Colors.muted)
                    ForEach(entry.heardAs, id: \.self) { variant in
                        HStack(spacing: 4) {
                            Text(variant)
                            Button {
                                vocabulary.removeVariant(variant, from: entry)
                            } label: {
                                Image(systemName: "xmark").font(.system(size: 8, weight: .bold))
                            }
                            .buttonStyle(.plain)
                            .foregroundStyle(DS.Colors.muted)
                            .accessibilityLabel("Ta bort \(variant)")
                        }
                        .font(.system(size: 12, weight: .medium))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(RoundedRectangle(cornerRadius: DS.Radius.chip, style: .continuous).fill(DS.Colors.chip))
                    }
                }
            }
            Spacer(minLength: 12)
            if entry.fixes > 0 {
                Text(entry.fixes == 1 ? String(localized: "Rättat 1 gång") : String(localized: "Rättat \(entry.fixes) gånger"))
                    .font(.system(size: 12))
                    .foregroundStyle(DS.Colors.muted)
            }
            Button {
                withAnimation(.snappy) { vocabulary.remove(entry) }
            } label: {
                Image(systemName: "trash").font(.system(size: 13))
            }
            .buttonStyle(.plain)
            .foregroundStyle(DS.Colors.muted)
            .opacity(hovering ? 1 : 0.7)
            .help("Ta bort \(entry.word)")
            .accessibilityLabel("Ta bort \(entry.word)")
        }
        .padding(.horizontal, 22)
        .padding(.vertical, 14)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
    }
}
