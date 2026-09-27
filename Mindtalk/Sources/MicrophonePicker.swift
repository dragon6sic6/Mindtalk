import CoreAudio
import SwiftUI

/// "Mikrofon" sheet: one card per mic. The chosen one opens up into a live
/// waveform with a plain-words check that we can hear you.
struct MicrophonePicker: View {
    @ObservedObject var mics: Microphones
    let close: () -> Void
    @State private var showOthers = false

    private var others: [InputDevice] { mics.devices.filter { !$0.isBuiltIn && !$0.isVirtual } }
    private var virtual: [InputDevice] { mics.devices.filter(\.isVirtual) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                Text("Mikrofon").font(.system(size: 30, weight: .regular, design: .serif))
                Spacer()
                Button(action: close) {
                    Image(systemName: "xmark")
                        .font(.system(size: 13, weight: .semibold))
                        .frame(width: 28, height: 28)
                        .background(Circle().fill(DS.Colors.chip))
                }
                .buttonStyle(.plain)
                .keyboardShortcut(.cancelAction)
                .accessibilityLabel("Stäng")
            }
            Text("Välj vad Mindtalk lyssnar med. Prata för att testa.")
                .font(.system(size: 14))
                .foregroundStyle(DS.Colors.muted)
                .padding(.top, 6)

            ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    if let builtIn = mics.builtIn {
                        option(.builtIn, icon: builtIn.icon, title: "Inbyggd mikrofon", detail: builtIn.name,
                               badge: "Rekommenderas")
                    }
                    option(.system, icon: "wand.and.stars", title: mics.systemName,
                           detail: "Följer ljudinställningarna på din Mac")
                    ForEach(others) { device in
                        option(.device(uid: device.uid), icon: device.icon, title: device.name,
                               detail: device.isBluetooth ? "Bluetooth – ljudet blir sämre medan mikrofonen används" : nil)
                    }
                    if !virtual.isEmpty {
                        Button {
                            withAnimation(.easeOut(duration: 0.15)) { showOthers.toggle() }
                        } label: {
                            HStack(spacing: 6) {
                                Image(systemName: "chevron.right")
                                    .font(.system(size: 11, weight: .semibold))
                                    .rotationEffect(.degrees(showOthers ? 90 : 0))
                                Text("Virtuella enheter (\(virtual.count))")
                            }
                            .font(.system(size: 13))
                            .foregroundStyle(DS.Colors.muted)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .padding(.top, 4)
                        if showOthers {
                            ForEach(virtual) { device in
                                option(.device(uid: device.uid), icon: device.icon, title: device.name,
                                       detail: "Från en annan app, t.ex. ett mötesprogram")
                            }
                        }
                    }
                }
                .padding(.vertical, 22)
            }
            .scrollContentBackground(.hidden)

            HStack {
                Label("Ljudet stannar på din Mac.", systemImage: "lock.fill")
                    .font(.system(size: 12))
                    .foregroundStyle(DS.Colors.muted)
                Spacer()
                Button("Klar", action: close)
                    .buttonStyle(.ink)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(28)
        .frame(width: 520, height: 580)
        .background(DS.Colors.paper)
        .onAppear {
            mics.refresh()
            if case .device(let uid) = mics.choice, virtual.contains(where: { $0.uid == uid }) { showOthers = true }
            mics.startMonitor()
        }
        .onDisappear { mics.stopMonitor() }
    }

    private func option(_ choice: MicChoice, icon: String, title: String, detail: String?,
                        badge: String? = nil) -> some View {
        let selected = mics.choice == choice
        return Button {
            withAnimation(.snappy(duration: 0.25)) { mics.choice = choice }
        } label: {
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .center, spacing: 14) {
                    Image(systemName: icon)
                        .font(.system(size: 16))
                        .foregroundStyle(selected ? DS.Colors.accent : .secondary)
                        .frame(width: 38, height: 38)
                        .background(Circle().fill(selected ? DS.Colors.accent.opacity(0.12) : DS.Colors.chip))
                    VStack(alignment: .leading, spacing: 3) {
                        HStack(spacing: 8) {
                            Text(LocalizedStringKey(title)).font(.system(size: 15, weight: .semibold)).lineLimit(1)
                            if let badge {
                                Text(LocalizedStringKey(badge))
                                    .font(.system(size: 11, weight: .semibold))
                                    .foregroundStyle(DS.Colors.accent)
                                    .padding(.horizontal, 7).padding(.vertical, 2)
                                    .background(Capsule().fill(DS.Colors.accent.opacity(0.12)))
                            }
                        }
                        if let detail {
                            Text(LocalizedStringKey(detail)).font(.system(size: 13)).foregroundStyle(DS.Colors.muted).lineLimit(1)
                        }
                    }
                    Spacer(minLength: 8)
                    Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: 18))
                        .foregroundStyle(selected ? DS.Colors.accent : DS.Colors.fieldStroke)
                }
                if selected {
                    VStack(alignment: .leading, spacing: 8) {
                        Waveform(levels: mics.history)
                            .frame(height: 34)
                        HStack(spacing: 6) {
                            let hearing = mics.hearing
                            Circle()
                                .fill(hearing.good == true ? DS.Colors.good : hearing.good == false ? Color.orange : DS.Colors.fieldStroke)
                                .frame(width: 7, height: 7)
                            Text(hearing.text).font(.system(size: 12)).foregroundStyle(DS.Colors.muted)
                        }
                    }
                    .transition(.opacity.combined(with: .move(edge: .top)))
                }
            }
            .padding(.horizontal, 18)
            .padding(.vertical, 16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(selected ? DS.Colors.field : DS.Colors.card))
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(selected ? DS.Colors.accent.opacity(0.7) : .clear, lineWidth: 1.5))
            .shadow(color: .black.opacity(selected ? 0.06 : 0), radius: 10, y: 3)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}
