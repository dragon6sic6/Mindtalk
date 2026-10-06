import SwiftUI

// MARK: - Ordlista page

struct VocabularyPage: View {
    @ObservedObject var vocabulary: Vocabulary
    @Environment(\.undoManager) private var undo
    @State private var word = ""
    @State private var heardAs = ""
    @State private var sample = ""
    @State private var search = ""
    @State private var highlighted: UUID?
    @FocusState private var wordFocused: Bool

    private var canAdd: Bool { Vocabulary.isValidWord(word) }

    private var shown: [VocabularyEntry] {
        let q = search.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return vocabulary.entries }
        return vocabulary.entries.filter { e in
            e.word.localizedCaseInsensitiveContains(q) || e.heardAs.contains { $0.localizedCaseInsensitiveContains(q) }
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            PageHeader(title: "Ordlista",
                       subtitle: "Namn och ord som Mindtalk ska stava som du vill. Delade eller felskrivna varianter – som ”Mind Talk” – rättas automatiskt.")
                .padding(.bottom, 12)
                .staggered(0)

            SectionTitle("Lägg till")
            .staggered(0)
            HStack(alignment: .bottom, spacing: 12) {
                field("Ord", placeholder: "t.ex. Mindtalk", text: $word)
                    .focused($wordFocused)
                field("Hörs ofta som (valfritt)", placeholder: "t.ex. mind talk, min tal", text: $heardAs)
                Button("Lägg till", action: add)
                    .buttonStyle(.ink)
                    .disabled(!canAdd)
            }
            .padding(20)
            .card()
            .staggered(0)

            HStack(alignment: .firstTextBaseline) {
                SectionTitle("Dina ord")
                Spacer()
                if vocabulary.entries.count > 8 {
                    TextField("Sök", text: $search)
                        .textFieldStyle(.roundedBorder)
                        .frame(width: 180)
                }
            }
            VStack(spacing: 0) {
                if vocabulary.entries.isEmpty {
                    Text("Inga ord än. Lägg till namn och begrepp du ofta säger.")
                        .font(.system(size: 13))
                        .foregroundStyle(DS.Colors.muted)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 22)
                        .padding(.vertical, 18)
                } else if shown.isEmpty {
                    Text("Inget ord matchar ”\(search)”.")
                        .font(.system(size: 13))
                        .foregroundStyle(DS.Colors.muted)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 22)
                        .padding(.vertical, 18)
                }
                ForEach(Array(shown.enumerated()), id: \.element.id) { i, entry in
                    if i > 0 { CardDivider() }
                    VocabularyRow(entry: entry, vocabulary: vocabulary, highlighted: highlighted == entry.id)
                        .transition(.asymmetric(insertion: .move(edge: .top).combined(with: .opacity),
                                                removal: .opacity))
                }
            }
            .card()
            .animation(.spring(duration: 0.4, bounce: 0.15), value: vocabulary.entries.map(\.id))

            SectionTitle("Prova ordlistan")
            .staggered(1)
            VStack(alignment: .leading, spacing: 12) {
                TextField("Skriv en mening som Mindtalk kunde ha hört, t.ex. ”jag använder mind talk”", text: $sample)
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

            if !vocabulary.entries.isEmpty {
                ListenerRow(status: .shared)
                    .staggered(1)
            }

            Text("Varianter rättas alltid – lägg bara till sådant du inte också säger på riktigt. Skilj flera varianter med komma. Vanliga ord (som ”per” eller ”test”) och text i versaler lämnas orörda.")
                .font(.system(size: 12))
                .foregroundStyle(DS.Colors.muted)
                .fixedSize(horizontal: false, vertical: true)
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
        guard canAdd else { return }
        var id: UUID?
        withAnimation(.spring(duration: 0.45, bounce: 0.2)) { id = vocabulary.add(word: word, heardAs: heardAs) }
        word = ""
        heardAs = ""
        search = ""
        wordFocused = true
        // Show where it went — also when it was merged into an existing word.
        highlighted = id
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.4) { if highlighted == id { highlighted = nil } }
    }

    /// The sample with every fixed word in bold.
    private var preview: AttributedString {
        let result = vocabulary.preview(sample)
        var attributed = AttributedString(result.text)
        for range in result.ranges {
            guard let r = Range(range, in: result.text),
                  let lower = AttributedString.Index(r.lowerBound, within: attributed),
                  let upper = AttributedString.Index(r.upperBound, within: attributed) else { continue }
            attributed[lower..<upper].foregroundColor = DS.Colors.accent
            attributed[lower..<upper].font = .system(size: 14, weight: .semibold)
        }
        return attributed
    }
}

private struct VocabularyRow: View {
    let entry: VocabularyEntry
    @ObservedObject var vocabulary: Vocabulary
    let highlighted: Bool
    @Environment(\.undoManager) private var undo
    @State private var hovering = false
    @State private var editing = false
    @State private var draft = ""
    @State private var addingVariant = false
    @State private var newVariant = ""
    @FocusState private var focus: Field?

    private enum Field { case word, variant }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 14) {
            VStack(alignment: .leading, spacing: 8) {
                if editing {
                    TextField("Ord", text: $draft)
                        .textFieldStyle(.roundedBorder)
                        .font(.system(size: 14, weight: .semibold))
                        .frame(maxWidth: 260)
                        .focused($focus, equals: .word)
                        .onSubmit(commitRename)
                        .onExitCommand { editing = false }
                } else {
                    Text(entry.word)
                        .font(.system(size: 15, weight: .semibold))
                        .onTapGesture(count: 2) { startRename() }
                        .help("Dubbelklicka för att ändra")
                }
                FlowLayout(spacing: 6) {
                    if !entry.heardAs.isEmpty {
                        Text("hörs ofta som").font(.system(size: 12)).foregroundStyle(DS.Colors.muted)
                    }
                    ForEach(entry.heardAs, id: \.self) { variant in
                        VariantChip(text: variant) {
                            withAnimation(.snappy) { vocabulary.removeVariant(variant, from: entry, undo: undo) }
                        }
                    }
                    if addingVariant {
                        TextField("variant", text: $newVariant)
                            .textFieldStyle(.roundedBorder)
                            .font(.system(size: 12))
                            .frame(width: 150)
                            .focused($focus, equals: .variant)
                            .onSubmit(commitVariant)
                            .onExitCommand { addingVariant = false }
                    } else {
                        Button {
                            newVariant = ""
                            addingVariant = true
                            focus = .variant
                        } label: {
                            Label("Variant", systemImage: "plus").font(.system(size: 12, weight: .medium))
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(DS.Colors.muted)
                        .opacity(hovering || entry.heardAs.isEmpty ? 1 : 0.6)
                        .help("Lägg till något Mindtalk ofta hör i stället")
                    }
                }
            }
            Spacer(minLength: 12)
            if entry.fixes > 0 {
                Text(entry.fixes == 1 ? String(localized: "Rättat 1 gång") : String(localized: "Rättat \(entry.fixes) gånger"))
                    .font(.system(size: 12))
                    .foregroundStyle(DS.Colors.muted)
            }
            Button(action: startRename) {
                Image(systemName: "pencil").font(.system(size: 13))
            }
            .buttonStyle(.plain)
            .foregroundStyle(DS.Colors.muted)
            .opacity(hovering ? 1 : 0)
            .help("Ändra \(entry.word)")
            .accessibilityLabel("Ändra \(entry.word)")
            Button {
                withAnimation(.snappy) { vocabulary.remove(entry, undo: undo) }
            } label: {
                Image(systemName: "trash").font(.system(size: 13))
            }
            .buttonStyle(.plain)
            .foregroundStyle(DS.Colors.muted)
            .opacity(hovering ? 1 : 0.7)
            .help("Ta bort \(entry.word) (⌘Z ångrar)")
            .accessibilityLabel("Ta bort \(entry.word)")
        }
        .padding(.horizontal, 22)
        .padding(.vertical, 14)
        .background(DS.Colors.chip.opacity(highlighted ? 0.8 : 0).animation(.easeOut(duration: 0.6), value: highlighted))
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onChange(of: focus) { _, now in
            // Clicking elsewhere ends editing.
            if now != .word, editing { commitRename() }
            if now != .variant, addingVariant { commitVariant() }
        }
    }

    private func startRename() {
        draft = entry.word
        editing = true
        focus = .word
    }

    private func commitRename() {
        guard editing else { return }
        editing = false
        if draft != entry.word { withAnimation(.snappy) { vocabulary.rename(entry, to: draft) } }
    }

    private func commitVariant() {
        guard addingVariant else { return }
        addingVariant = false
        for v in newVariant.split(separator: ",") { vocabulary.addVariant(String(v), to: entry) }
    }
}

private struct VariantChip: View {
    let text: String
    let remove: () -> Void
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 5) {
            Text(text)
            Button(action: remove) {
                Image(systemName: "xmark")
                    .font(.system(size: 9, weight: .bold))
                    .frame(width: 16, height: 16)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(hovering ? AnyShapeStyle(.primary) : AnyShapeStyle(DS.Colors.muted))
            .help("Ta bort ”\(text)” (⌘Z ångrar)")
            .accessibilityLabel("Ta bort \(text)")
        }
        .font(.system(size: 12, weight: .medium))
        .padding(.leading, 8)
        .padding(.trailing, 3)
        .padding(.vertical, 2)
        .background(RoundedRectangle(cornerRadius: DS.Radius.chip, style: .continuous).fill(DS.Colors.chip))
        .onHover { hovering = $0 }
    }
}

/// Lays its children out in rows, wrapping when a row is full.
struct FlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(subviews, width: proposal.width ?? .infinity)
        let width = rows.map { $0.width }.max() ?? 0
        let height = rows.reduce(0) { $0 + $1.height } + spacing * CGFloat(max(0, rows.count - 1))
        return CGSize(width: width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for row in arrange(subviews, width: bounds.width) {
            var x = bounds.minX
            for index in row.items {
                let size = subviews[index].sizeThatFits(.unspecified)
                subviews[index].place(at: CGPoint(x: x, y: y + (row.height - size.height) / 2), proposal: .unspecified)
                x += size.width + spacing
            }
            y += row.height + spacing
        }
    }

    private func arrange(_ subviews: Subviews, width: CGFloat) -> [(items: [Int], width: CGFloat, height: CGFloat)] {
        var rows: [(items: [Int], width: CGFloat, height: CGFloat)] = []
        var current: (items: [Int], width: CGFloat, height: CGFloat) = ([], 0, 0)
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            let needed = current.items.isEmpty ? size.width : current.width + spacing + size.width
            if needed > width, !current.items.isEmpty {
                rows.append(current)
                current = ([index], size.width, size.height)
            } else {
                current.items.append(index)
                current.width = needed
                current.height = max(current.height, size.height)
            }
        }
        if !current.items.isEmpty { rows.append(current) }
        return rows
    }
}

/// The listener that makes the speech model hear your words — and where it stands.
private struct ListenerRow: View {
    @ObservedObject var status: VocabularyBoostStatus

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 15))
                .foregroundStyle(status.state == .ready ? DS.Colors.good : DS.Colors.muted)
                .frame(width: 20)
                .padding(.top, 1)
            VStack(alignment: .leading, spacing: 4) {
                Text("Talmodellen hör dina ord").font(.system(size: 13, weight: .semibold))
                Text(detail).font(.system(size: 12.5)).foregroundStyle(DS.Colors.muted)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            if case .failed = status.state {
                Button("Försök igen") { Vocabulary.shared.prepareListener() }.buttonStyle(.soft)
            }
        }
        .padding(18)
        .card()
    }

    private var symbol: String {
        switch status.state {
        case .ready: "ear.fill"
        case .downloading: "arrow.down.circle"
        case .missing: "ear"
        case .failed: "exclamationmark.triangle"
        }
    }

    private var detail: String {
        switch status.state {
        case .ready:
            return String(localized: "När något du dikterar liknar ett ord i listan lyssnar Mindtalk en gång till efter just det ordet, så att namn stavas rätt redan från början. Allt sker på din Mac.")
        case .downloading:
            return String(localized: "Laddar ned lyssnaren (\(VocabularyBoost.sizeText)) – en gång, sedan fungerar den offline.")
        case .missing:
            return String(localized: "En liten lyssnare (\(VocabularyBoost.sizeText)) laddas ned första gången, så att namn stavas rätt redan från början.")
        case .failed(let why):
            return String(localized: "Lyssnaren kunde inte laddas ned: \(why) Dina ord rättas i texten som vanligt.")
        }
    }
}
