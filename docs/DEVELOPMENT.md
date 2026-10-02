# Developing Mindtalk

## Requirements

- A Mac with Apple Silicon and macOS 26 or later
- Xcode 26 or later
- [XcodeGen](https://github.com/yonaskolb/XcodeGen): `brew install xcodegen`

The Xcode project is generated from `project.yml` and isn't checked in.

```bash
make run       # Debug build and launch
make install   # Release build into /Applications (development signature)
make install-signed   # Same, signed with Developer ID — keeps macOS permissions across builds and updates
make open      # Generate the project and open it in Xcode
make dmg       # Signed, notarized DMG in dist/ (scripts/release.sh)
```

## Self-tests

These run against the same code the app uses. File transcription and text cleanup
do not need a microphone; the recorder test below exercises live microphone input.

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

# Live microphone startup, repeated start/stop, input selection and level preview (Debug only)
$BIN --record-test
# Repeat more often, optionally including an available physical input's numeric Core Audio ID
$BIN --record-test --record-test-cycles 10 --record-test-device 42
```

The recorder test requests microphone permission and checks the built-in microphone
(when available) and the system default. It reuses recorders between selections,
starts each twice to check that audio is retained, verifies finite 16 kHz samples
and level callbacks, and checks that the microphone picker's preview keeps no
audio. It also checks recovery on the same recorder after an invalid device fails
to start, and releases a running recorder from its audio callback to verify that
the recorder is released and callbacks stop. Each
selection records for about a second after the first audio callback.
Audio stays in memory and is discarded; no speech models, text insertion or saved
microphone preferences are involved. Add `--record-test-device` to cover a connected
USB or Bluetooth microphone without changing the system input. The test exits
with status 0 on success and 1 on failure.

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

## Updates (Sparkle)

Mindtalk updates itself with [Sparkle](https://sparkle-project.org). The app reads
`releases/latest/download/appcast.xml` from this repository once a day (setting in
Settings → App). `scripts/release.sh` writes `dist/appcast-<version>/appcast.xml`,
signed with Mindtalk's EdDSA key; upload it to the release together with `Mindtalk.dmg`.

- The private key is in the login keychain, account `mindtalk`. Back it up
  (`generate_keys --account mindtalk -x key.txt`) — without it no update can be signed.
- The public key is `SUPublicEDKey` in `project.yml`.
- Every release needs a higher `CURRENT_PROJECT_VERSION`; that is what Sparkle compares.
- Release notes: put them in `dist/notes-<version>.md` before running the script.
- The feed is only reachable while the repository is public.

## Licences in the app

FluidAudio is compiled into Mindtalk, so its licence and third-party notices ship
in the app (`Mindtalk/Resources/Acknowledgements.txt`, shown under Settings → About →
Show Licences). `scripts/release.sh` regenerates it; by hand after a FluidAudio update:

```bash
make build && python3 scripts/acknowledgements.py
```

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
| `MainView.swift` | The window: Dictation, Recent, Settings |
| `TabBar.swift` | The floating tab bar that widens when you point at it |
| `StatusPanel.swift` | The menu bar panel |
| `OnboardingView.swift` | First launch |
| `Dictation.swift` | Hold / double-tap / lock logic; record → transcribe → paste |
| `Hotkey.swift` | Global key listener (event tap) and key choice |
| `Recorder.swift` | Audio Queue capture from selected device UID → 16 kHz mono |
| `SpeechModels.swift` | The models: pinned files, download, verification, inference |
| `TextCleanup.swift` | Filler removal, voice commands, AI polish with guard rails |
| `Vocabulary.swift`, `VocabularyPage.swift` | Vocabulary and its page |
| `MediaControl.swift` | Pausing media / fading sound while dictating |
| `TextInserter.swift` | Pastes with ⌘V and restores the clipboard (or keeps the text there) |
| `Microphones.swift`, `MicrophonePicker.swift` | Input devices (Core Audio) and the picker |
| `Stats.swift`, `StatsCard.swift` | Statistics and the week chart |
| `HUD.swift` | The dictation pill at the bottom of the screen |
| `Settings.swift` | Preferences: key, mode, appearance, sounds, language |
| `DesignSystem.swift`, `Motion.swift`, `Mark.swift` | Colours, components, motion, the mark |
| `PolishGuard.swift` | Accepts the AI polish only if it merely removes words — never adds, never drops a negation |
| `VocabularyMatcher.swift` | Vocabulary matching in one pass (self-contained, testable) |
| `SecureInput.swift` | Is the cursor in a password field? |
| `FocusedField.swift` | Is there a text field to paste into? If clearly not, the text goes to the clipboard |
| `Updates.swift` | Sparkle: signed automatic updates |
| `AppMover.swift` | Offers to move itself into Applications when run from the DMG or Downloads |
| `Demo.swift` | Demo mode for screenshots (Debug builds only) |
