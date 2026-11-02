# Mindtalk for Windows

A Windows port of Mindtalk: hold a key, speak, let go — your words are typed where
your cursor is. Everything runs on-device.

It follows Mindtalk 2.3 for Mac one-to-one:

- **Dictation**: the pill, hold or double-tap, vocabulary, media pausing, sounds, statistics.
- **Meetings**: live text, who says what, voices it recognizes, a round of names, notes and
  marks, call sound from Teams, Zoom, Meet and the like, imported recordings, summaries with
  decisions and to-dos, search, folders, exports (PDF, Word, Markdown, text, subtitles, audio).
- **Ask** about one meeting or all of them, with answers that point to the moment.
- **Thoughts**: hold left Alt and think out loud; they're saved on the Thoughts page.
- **AI apps**: Claude, ChatGPT, Cursor and others can read your meetings and thoughts (MCP),
  and so can the `mindtalk` command line — only when you turn it on, and every read is logged.
- **Home**, the sidebar, **Settings in five sections**, the **7-step introduction**, the
  tray panel (the Mac's menu bar panel) and the new dot-sphere icon.

Where Windows works differently, the closest Windows equivalent is used — see
[Differences from the Mac](#differences-from-the-mac).

## Stack

- C# / .NET 9, WinUI 3 (Windows App SDK), unpackaged and self-contained
- [ONNX Runtime](https://onnxruntime.ai) running Parakeet TDT 0.6B v3 on the CPU (the Mac app uses Core ML conversions, which don't run on Windows)
- [NAudio](https://github.com/naudio/NAudio) for WASAPI capture and the cues
- [Velopack](https://velopack.io) for the installer and updates (the Mac uses Sparkle)
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) for telling speakers apart and voiceprints
- [LLamaSharp](https://github.com/SciSharp/LLamaSharp) (llama.cpp, Vulkan or CPU) running Gemma 4 for summaries and Ask
- [Open XML SDK](https://github.com/dotnet/Open-XML-SDK) and [PDFsharp](https://www.pdfsharp.com) for Word and PDF exports

| Project | What |
|---|---|
| `src/Mindtalk.App` | The app: HUD, tray icon and panel, sidebar and pages (Home, Dictation, Meetings, Thoughts, Vocabulary), meetings (`Meetings/`), Settings (`Settings/`), introduction, design system, updates |
| `src/Mindtalk.Core` | Platform-independent: text cleanup, hotkey routing, the dictation flow, vocabulary, statistics, model download, the Parakeet engine; meetings (store, engine, speakers, final pass, exports), thoughts, voices, summaries and Ask, the AI apps' tools |
| `src/Mindtalk.Platform` | Windows: keyboard hook, global shortcuts, WASAPI recorder, meeting audio (microphone + process loopback), call detection, microphones, clipboard and paste, focus detection (UI Automation), media control, spell check, cues |
| `src/Mindtalk.Writers` | The writers: Gemma 4 (downloaded, run locally), Ollama and LM Studio (on 127.0.0.1 only) |
| `src/Mindtalk.Mcp` | `mindtalk-mcp.exe`: the MCP server AI apps start, and the `mindtalk` command line |
| `src/Mindtalk.Cli` | Self-tests (see below) |
| `tests/Mindtalk.Core.Tests` | xunit tests |

## Speech models

Downloaded once to `%LOCALAPPDATA%\Mindtalk\Models`, every file checked against a pinned SHA-256.

| | Model | Source | Size |
|---|---|---|---|
| Swedish | Klang Pianissimo (int8) | [KlangAI/pianissimo-sv-onnx](https://huggingface.co/KlangAI/pianissimo-sv-onnx) | 661 MB |
| Multilingual | Parakeet Ultra (int8) | [Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx](https://huggingface.co/Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx) (community export) | 668 MB |

For meetings and summaries, also downloaded on first use:

| | Model | Size |
|---|---|---|
| Speakers | pyannote segmentation 3.0 + 3D-Speaker CAM++ (sherpa-onnx exports) | 34 MB |
| Summaries, Ask (default) | Gemma 4 E4B, GGUF Q4_K_M | 5.0 GB |
| Summaries, Ask (larger) | Gemma 4 26B A4B, GGUF Q4_K_M | 16.9 GB |

Settings, statistics, the vocabulary and recent dictations are in `%APPDATA%\Mindtalk`;
meetings (`Meetings\<id>\`), thoughts and voices in `%LOCALAPPDATA%\Mindtalk`. None of it
leaves the PC: Ollama and LM Studio are only reached on 127.0.0.1, and AI apps only read
what you allow, through a local pipe. Unhandled errors are logged to `%LOCALAPPDATA%\Mindtalk\crash.log`.

## Keys

Same behaviour as the Mac app. Right Alt by default for dictation, left Alt for thoughts; any key or combination of sided
modifiers can be picked in Settings.

| | |
|---|---|
| Hold your shortcut | Talk; let go to type |
| Double-tap your shortcut | Lock hands-free; tap again (or click ✓ on the pill) to finish |
| <kbd>Space</kbd> while holding | Lock hands-free |
| <kbd>Esc</kbd> | Cancel without typing anything (also closes the tray panel) |
| Hold left <kbd>Alt</kbd> | Say a thought (tap to lock, <kbd>Esc</kbd> cancels); <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>T</kbd> while dictation uses left Alt |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>V</kbd> | Paste the last dictation again (the Mac's ⌃⌥V) |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>M</kbd> | Start or end a meeting, from any app (⌃⌥⌘M) |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>P</kbd> | Pause or resume the meeting (⌥⌘P) |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>1</kbd> / <kbd>2</kbd> / <kbd>3</kbd> | Mark a decision, a to-do, something important (⌥⌘1/2/3) |
| <kbd>Ctrl</kbd>+<kbd>K</kbd>, <kbd>Ctrl</kbd>+<kbd>O</kbd>, <kbd>Ctrl</kbd>+<kbd>F</kbd> | Ask, import a recording, search (Meetings) |
| <kbd>Ctrl</kbd>+<kbd>1</kbd> / <kbd>2</kbd> | Switch language (tray panel open) |
| <kbd>Ctrl</kbd>+<kbd>1</kbd>–<kbd>5</kbd>, <kbd>Ctrl</kbd>+<kbd>,</kbd> | Pages and Settings in the main window |

⌥⌘ becomes Ctrl+Shift rather than Ctrl+Alt: on many keyboards Ctrl+Alt+digit is AltGr
and types a character (AltGr+2 → @). Meeting and paste shortcuts can be changed or
turned off in Settings → General.

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
| Apple Intelligence as a writer | Windows AI, greyed out | Same reason as polish above. Gemma 4 E4B is the default, as on the Mac. |
| Gemma 4 as MLX files (6.9 / 15.6 GB) | Gemma 4 as GGUF Q4_K_M (5.0 / 16.9 GB) via llama.cpp | MLX only runs on Apple silicon. |
| Speaker models (Nemotron 3, ReDimNet2+) | pyannote + CAM++ via sherpa-onnx | What runs well on Windows CPUs. Voices remembered on a Mac aren't recognized on Windows (different voiceprints). |
| Call sound (Core Audio taps) | Process loopback (WASAPI) | For a browser call, every tab's sound is included. |
| `mindtalk` on PATH | `mindtalk-mcp.exe <command>` in the app folder | A `mindtalk` command next to `Mindtalk.exe` would clash on Windows' case-insensitive file system. |

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

# Meetings
dotnet run --project src/Mindtalk.Cli -- --speakers-install
dotnet run --project src/Mindtalk.Cli -- --meeting-file call.m4a --out C:\temp\m   # import: speakers, text, exports
dotnet run --project src/Mindtalk.Cli -- --meeting-live --seconds 30 --round        # record from the microphone
dotnet run --project src/Mindtalk.Cli -- --calls --seconds 30                       # call apps starting and ending
dotnet run --project src/Mindtalk.Cli -- --ai-help                                  # Gemma, summaries, Ask self-tests

# What AI apps see (with Mindtalk running and Settings → AI apps on)
mindtalk-mcp.exe meetings | meeting <id> | search <words> | thoughts | tasks [--json]
```

Debug builds of the app take the Mac's screenshot flags. `--demo` fills Recent, the
statistics and the vocabulary with example data in a folder of its own (yours are never
read or written) and treats the app as ready:

```powershell
Mindtalk.exe --demo [--dark|--light] [--sv|--en] [--page settings] [--size 960x1300] [--scroll 1200]
Mindtalk.exe --demo --show-panel
Mindtalk.exe --demo --page home|dictation|meetings|thoughts|vocabulary|settings [--section general|language|dictation|meetings|aiapps]
Mindtalk.exe --demo --demo-meeting 0 [--demo-tab text] [--demo-player]   # a meeting's page
Mindtalk.exe --demo --demo-live-meeting | --demo-round-of-names | --demo-new-voice | --demo-finishing
Mindtalk.exe --demo --demo-meeting-panel | --demo-call-box | --demo-ask "question" | --demo-search words | --demo-empty
Mindtalk.exe --demo --demo-thought | --demo-saved | --ai-apps-on
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
| `MainView`, sidebar, Home, Dictation, Thoughts, Vocabulary | `MainWindow`, `Controls/Sidebar`, `Pages/*` |
| Meetings, a meeting, the live meeting, the floating panel, Ask | `Meetings/*` (`MeetingHub` ties them together) |
| The meeting engine | `Core/Meetings/Engine/*` (`MeetingEngine`, `LiveMeeting`, `FinalPass`) + `Platform/Meetings/*` |
| Summaries, Ask, writers | `MeetingSummarizer`, `MeetingAsk`, `Mindtalk.Writers` |
| AI apps (MCP), `mindtalk` | `AI/AiAppsService` (named pipe) + `Mindtalk.Mcp` |
| Settings | `Settings/*`, one page per section |
| `OnboardingView` | `Onboarding/*` |
| `DesignSystem`, `Motion`, `Mark` | `Styles/Design.xaml`, `Controls/*` |
| `Updates` (Sparkle) | `Updates` (Velopack) |
