# Mindtalk for Windows

A Windows port of Mindtalk: hold a key, speak, let go — your words are typed where
your cursor is. Everything runs on-device.

It follows the Mac app one-to-one: the same dictation pill, introduction, tray panel
(the Mac's menu bar panel), main window with its four pages, statistics, recent
dictations, vocabulary, media pausing, sounds, appearance and language settings,
and updates. Where Windows works differently, the closest Windows equivalent is used
— see [Differences from the Mac](#differences-from-the-mac).

## Stack

- C# / .NET 9, WinUI 3 (Windows App SDK), unpackaged and self-contained
- [ONNX Runtime](https://onnxruntime.ai) running Parakeet TDT 0.6B v3 on the CPU (the Mac app uses Core ML conversions, which don't run on Windows)
- [NAudio](https://github.com/naudio/NAudio) for WASAPI capture and the cues
- [Velopack](https://velopack.io) for the installer and updates (the Mac uses Sparkle)

| Project | What |
|---|---|
| `src/Mindtalk.App` | The app: HUD, tray icon and panel, main window and pages, introduction, design system, updates |
| `src/Mindtalk.Core` | Platform-independent: text cleanup, hotkey routing, the dictation flow, vocabulary, AI-polish guard, statistics, recent dictations, model catalog/download, the Parakeet engine |
| `src/Mindtalk.Platform` | Windows: keyboard hook, WASAPI recorder, microphones, clipboard and paste, focus/password-field detection (UI Automation), media control, spell check, cues |
| `src/Mindtalk.Cli` | Self-tests (see below) |
| `tests/Mindtalk.Core.Tests` | xunit tests |

## Speech models

Downloaded once to `%LOCALAPPDATA%\Mindtalk\Models`, every file checked against a pinned SHA-256.

| | Model | Source | Size |
|---|---|---|---|
| Swedish | Klang Pianissimo (int8) | [KlangAI/pianissimo-sv-onnx](https://huggingface.co/KlangAI/pianissimo-sv-onnx) | 661 MB |
| Multilingual | Parakeet Ultra (int8) | [Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx](https://huggingface.co/Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx) (community export) | 668 MB |

Settings, statistics, the vocabulary and recent dictations are in `%APPDATA%\Mindtalk`;
none of it leaves the PC. Unhandled errors are logged to `%LOCALAPPDATA%\Mindtalk\crash.log`.

## Keys

Same behaviour as the Mac app. Right Alt by default; any key or combination of sided
modifiers can be picked in Settings.

| | |
|---|---|
| Hold your shortcut | Talk; let go to type |
| Double-tap your shortcut | Lock hands-free; tap again (or click ✓ on the pill) to finish |
| <kbd>Space</kbd> while holding | Lock hands-free |
| <kbd>Esc</kbd> | Cancel without typing anything (also closes the tray panel) |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>V</kbd> | Paste the last dictation again (the Mac's ⌃⌥V) |
| <kbd>Ctrl</kbd>+<kbd>1</kbd> / <kbd>2</kbd> | Switch language (tray panel open) |
| <kbd>Ctrl</kbd>+<kbd>1</kbd>–<kbd>3</kbd>, <kbd>Ctrl</kbd>+<kbd>,</kbd> | Pages and Settings in the main window |

Modifiers pass through, so AltGr characters (AltGr+2 → @) still type — and cancel the
dictation that the press started. A lone Alt or Win press is masked so letting go
doesn't open the app's menu bar or Start.

## Differences from the Mac

| Mac | Windows | Why |
|---|---|---|
| Accessibility permission | Not needed | Windows lets apps read keys and paste without it. The introduction's permission step shows the microphone and "Open at login" instead. |
| Apple Intelligence polish | Windows' on-device model (Phi Silica) through the Windows App SDK | Only Copilot+ PCs have it, and Windows only lets packaged apps use it — so in this unpackaged build the setting explains that it isn't available. |
| Ordlistan's listener (Parakeet CTC 110M via FluidAudio) | Not yet | Needs an ONNX export of the CTC model and a port of FluidAudio's keyword spotting and rescoring. Your words are still fixed in the text, as on the Mac. |
| Spell checker for "ordinary words" (Swedish + English, always there) | Windows' spell checker | Swedish is only there with Windows' Swedish typing features installed; without them, short Swedish words that look like one of your vocabulary words may be recased. |
| SF Pro, New York, SF Symbols | Segoe UI Variable, Sitka, Segoe Fluent Icons | Apple's fonts and symbols may only be used on Apple platforms. |
| Tink / Pop / Glass | Soft sounds synthesized by Mindtalk | Apple's system sounds can't be shipped. |
| Menu bar item and panel | Tray icon and panel | |
| Show in Dock | Show in taskbar (closing minimizes instead of hiding) | |
| Move to Applications | The installer | |
| Sparkle | Velopack | |

## Develop

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download). No Visual Studio needed.

```powershell
cd windows
dotnet test
dotnet run --project src/Mindtalk.App -- --show          # the app (Debug)

dotnet run --project src/Mindtalk.Cli -- --clean "Eh, vi ses på på fredag ny rad tack"
dotnet run --project src/Mindtalk.Cli -- --install --model swedish
dotnet run --project src/Mindtalk.Cli -- --transcribe sample.wav --model swedish --clean
dotnet run --project src/Mindtalk.Cli -- --record-test    # live microphone, no model needed
dotnet run --project src/Mindtalk.Cli -- --dictate --seconds 5
dotnet run --project src/Mindtalk.Cli -- --mics           # microphones and how they're classified
dotnet run --project src/Mindtalk.Cli -- --focus --seconds 10   # what UI Automation reports in front
dotnet run --project src/Mindtalk.Cli -- --media-list     # what's playing (read-only)
dotnet run --project src/Mindtalk.Cli -- --spell per klass kaffe
dotnet run --project src/Mindtalk.Cli -- --cue start
```

Debug builds of the app take the Mac's screenshot flags. `--demo` fills Recent, the
statistics and the vocabulary with example data in a folder of its own (yours are never
read or written) and treats the app as ready:

```powershell
Mindtalk.exe --demo [--dark|--light] [--sv|--en] [--page settings] [--size 960x1300] [--scroll 1200]
Mindtalk.exe --demo --show-panel
Mindtalk.exe --demo --onboarding-step welcome|language|permissions|key|tryIt
Mindtalk.exe --demo-hud [--demo-locked] [--demo-transcribing] [--demo-language] [--demo-failed] [--demo-copied]
Mindtalk.exe --test-audio file.wav     # every recording "hears" that file instead of the microphone
Mindtalk.exe --models-dir C:\temp\m    # a first install, without touching your models
Mindtalk.exe --render-tray C:\temp\t   # the tray icons, every state, as PNG files
```

## Release

```powershell
dotnet tool install -g vpk
./scripts/release.ps1 -Version 1.0.0                                   # Setup.exe + update packages in artifacts/
./scripts/release.ps1 -Version 1.0.0 -Token $env:GITHUB_TOKEN -SignParams "…"   # and publish to GitHub
```

Installed copies look for new releases on github.com/dragon6sic6/Mindtalk once a day (if
allowed) and on "Check Now". Sign releases with a code-signing certificate, or Windows
SmartScreen warns on first run.

## Mac → Windows map

| Mac | Windows |
|---|---|
| Core ML via FluidAudio | ONNX Runtime (`ParakeetEngine`) |
| `TextCleanup.swift` | `TextCleanup.cs` |
| `KeyListener` (event tap) | `KeyRouter` (logic, tested) + `KeyboardHook` (`WH_KEYBOARD_LL` on its own thread) |
| `Dictation` | `DictationController` (+ `AppModel` for what the windows show) |
| `Recorder`, `Microphones` | `WasapiRecorder`, `Microphones` |
| `TextInserter` (⌘V, clipboard restored) | `TextInserter` (Ctrl+V via `SendInput`, clipboard restored, kept out of clipboard history) |
| `FocusedField`, `SecureInput` (Accessibility API) | `FocusInspector` (UI Automation), `OwnFocus` for Mindtalk's own windows |
| `MediaControl` (Core Audio + MediaRemote) | `MediaControl` (WASAPI sessions + System Media Transport Controls) |
| `Vocabulary`, `VocabularyMatcher` | `VocabularyStore`, `VocabularyMatcher` |
| `PolishGuard`, `Polisher` | `PolishGuard`, `LinePolisher` + `OnDevicePolisher` |
| `Stats` | `StatsStore` |
| `HUD` | `HudWindow` (transparent, topmost, never takes focus) |
| `StatusPanel` | `StatusPanel` (tray panel) |
| `MainView`, `TabBar`, `StatsCard`, `VocabularyPage`, `MicrophonePicker` | `MainWindow`, `FloatingTabBar`, `Pages/*` |
| `OnboardingView` | `Onboarding/*` |
| `DesignSystem`, `Motion`, `Mark` | `Styles/Design.xaml`, `Controls/*` |
| `Updates` (Sparkle) | `Updates` (Velopack) |
