# Developing Mindtalk

## Requirements

- A Mac with Apple Silicon and macOS 26 or later
- Xcode 26 or later
- [XcodeGen](https://github.com/yonaskolb/XcodeGen): `brew install xcodegen`

The Xcode project is generated from `project.yml` and isn't checked in.

```bash
make run       # Debug build and launch
make install   # Release build into /Applications
make open      # Generate the project and open it in Xcode
make dmg       # Signed, notarized DMG in dist/ (scripts/release.sh)
```

## Self-tests

All of these run against the same code the app uses, without a microphone.

```bash
BIN=build/Build/Products/Debug/Mindtalk.app/Contents/MacOS/Mindtalk

# Speech recognition from a file (swedish or multilingual). --clean adds the cleanup and the AI polish.
say -v Alva -o /tmp/t.aiff "Hej, det här är ett test ny rad hälsningar Anna"
$BIN --transcribe /tmp/t.aiff --model swedish --clean

# Only the text cleanup
$BIN --clean "Eh, vi ses på på fredag, nej jag menar lördag ny rad tack"

# Install a model from the terminal
$BIN --install multilingual

# The key logic: hold / tap / double-tap / space in every mode (Debug only; types nothing)
open -n -W --stdout /dev/stdout build/Build/Products/Debug/Mindtalk.app --args --simulate-keys

# Pause/fade whatever is playing for three seconds (Debug only)
$BIN --media-test
```

## Screenshots

The images in the README come from the Debug build in demo mode: example history and statistics in memory, nothing read from or written to your own data.

```bash
make build
scripts/screenshots/shoot.sh en    # and: sv
```

It captures every view in light and dark mode into `docs/images/<lang>/` and composes the hero images (`scripts/screenshots/hero.swift`). The terminal needs Screen Recording permission, and the screen must be awake.

## Releasing

`scripts/release.sh` builds for Apple Silicon, signs with Developer ID (hardened runtime, secure timestamp), lays out the DMG window with [dmgbuild](https://github.com/dmgbuild/dmgbuild) (background drawn by `scripts/dmg/background.swift`), signs the DMG, notarizes it and staples the ticket.

Notarization uses a notarytool profile in the keychain. Create it once — you'll be asked for an app-specific password from [account.apple.com](https://account.apple.com):

```bash
xcrun notarytool store-credentials Mindtalk --apple-id <apple-id> --team-id <team-id>
```

`NOTARIZE=0 scripts/release.sh` skips notarization (the DMG then only opens on your own Mac).

## Localization

The interface is Swedish and English, in `Mindtalk/Resources/Localizable.xcstrings`, with Swedish as the source language. Write new text in Swedish in code (`Text("…")`, `String(localized: "…")`), run `make build`, then:

```bash
python3 scripts/strings.py
```

It pulls in new strings, fills in the Swedish values (required — otherwise a Swedish Mac falls back to English) and lists anything still missing an English translation.

## Design

- **Colour:** black and white — a black accent in light mode, white in dark (`DesignSystem.swift`, the `AccentColor` asset). Red means recording and green means done; nothing else is coloured.
- **The mark:** speech becoming text — a sound wave that settles into a line of text and ends in a cursor (`Sources/Mark.swift`). The same geometry draws the app icon's layers, the living logo and the menu bar icon.
- **The icon:** an Icon Composer file (`Mindtalk/Resources/AppIcon.icon`) — a black gradient with two glass layers. macOS draws Liquid Glass and the dark and tinted variants. The layers are rendered by `scripts/make_icon_layers.swift`.

## Source map

| File | What |
|------|------|
| `App.swift` | Menu bar item, main menu, windows, Dock, login item, self-tests |
| `MainView.swift` | The window: sidebar, Dictation, Recent, Settings |
| `StatusPanel.swift` | The menu bar panel |
| `OnboardingView.swift` | First launch |
| `Dictation.swift` | Hold / double-tap / lock logic; record → transcribe → paste |
| `Hotkey.swift` | Global key listener (event tap) and key choice |
| `Recorder.swift` | Microphone → 16 kHz mono |
| `SpeechModels.swift` | The models: pinned files, download, verification, inference |
| `TextCleanup.swift` | Filler removal, voice commands, AI polish with guard rails |
| `Vocabulary.swift`, `VocabularyPage.swift` | Vocabulary and its page |
| `MediaControl.swift` | Pausing media / fading sound while dictating |
| `TextInserter.swift` | Pastes with ⌘V and restores the clipboard |
| `Microphones.swift`, `MicrophonePicker.swift` | Input devices (Core Audio) and the picker |
| `Stats.swift`, `StatsCard.swift` | Statistics and the week chart |
| `HUD.swift` | The dictation pill at the bottom of the screen |
| `Settings.swift` | Preferences: key, mode, appearance, sounds, language |
| `DesignSystem.swift`, `Motion.swift`, `Mark.swift` | Colours, components, motion, the mark |
| `Demo.swift` | Demo mode for screenshots (Debug builds only) |
