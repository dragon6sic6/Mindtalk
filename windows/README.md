# Mindtalk for Windows

A Windows port of Mindtalk: hold a key, speak, let go — your words are typed where
your cursor is. Everything runs on-device.

**Status: early, but the core loop works.** A tray app with a global push-to-talk key,
microphone capture, on-device transcription, text cleanup and paste (with your
clipboard put back afterwards). No installer, onboarding, vocabulary, recent dictations,
statistics or auto-update yet.

## Stack

- C# / .NET 9, WinUI 3 (Windows App SDK), unpackaged and self-contained
- [ONNX Runtime](https://onnxruntime.ai) running Parakeet TDT 0.6B v3 on the CPU (the Mac app uses Core ML conversions, which don't run on Windows)
- [NAudio](https://github.com/naudio/NAudio) for WASAPI capture

| Project | What |
|---|---|
| `src/Mindtalk.App` | The tray app: tray icon, HUD, settings window, wiring |
| `src/Mindtalk.Core` | Platform-independent: text cleanup, hotkey routing, the dictation state machine, model catalog/download, the Parakeet engine |
| `src/Mindtalk.Platform` | Windows: keyboard hook, WASAPI recorder, clipboard and paste |
| `src/Mindtalk.Cli` | Self-tests: `--clean`, `--install`, `--transcribe`, `--record-test`, `--dictate` |
| `tests/Mindtalk.Core.Tests` | xunit tests (cleanup, key routing, hold / tap / double-tap / Space / Esc) |

## Speech models

Downloaded once to `%LOCALAPPDATA%\Mindtalk\Models`, every file checked against a pinned SHA-256.

| | Model | Source | Size |
|---|---|---|---|
| Swedish | Klang Pianissimo (int8) | [KlangAI/pianissimo-sv-onnx](https://huggingface.co/KlangAI/pianissimo-sv-onnx) | ~660 MB |
| Multilingual | Parakeet Ultra (int8) | [Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx](https://huggingface.co/Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx) (community export) | ~670 MB |

Settings are in `%APPDATA%\Mindtalk\settings.json`.

## Keys

Same behaviour as the Mac app. Right Alt by default; any key or combination of sided
modifiers can be picked in the window.

| | |
|---|---|
| Hold your shortcut | Talk; let go to type |
| Double-tap your shortcut | Lock hands-free; tap again to finish |
| <kbd>Space</kbd> while holding | Lock hands-free |
| <kbd>Esc</kbd> | Cancel without typing anything |

Modifiers pass through, so AltGr characters (AltGr+2 → @) still type — and cancel the
dictation that the press started. A lone Alt or Win press is masked so letting go
doesn't open the app's menu bar or Start.

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
```

Debug builds of the app also take `--test-audio file.wav`: every recording "hears" that
file instead of the microphone, so the whole path (key → HUD → transcription → paste)
can be checked with synthetic key presses and no one speaking.

## Mac → Windows map

| Mac | Windows |
|---|---|
| Core ML via FluidAudio | ONNX Runtime (`ParakeetEngine`) |
| `TextCleanup.swift` | `TextCleanup.cs` |
| `KeyListener` (Core Graphics event tap) | `KeyRouter` (logic, tested) + `KeyboardHook` (`WH_KEYBOARD_LL` on its own thread) |
| `Dictation` key handling | `DictationController` |
| Audio Queue | WASAPI via NAudio (`WasapiRecorder`) |
| Paste with ⌘V, clipboard restored | Ctrl+V via `SendInput`; clipboard snapshot restored after 1 s, kept out of clipboard history |
| Menu bar item and panel | Tray icon and menu |
| HUD | `HudWindow` (topmost, never takes focus) |
| Apple Intelligence polish | — (no on-device equivalent yet) |
| Sparkle | — (todo) |

## Known gaps

- Password fields aren't detected yet (the Mac app never keeps text dictated into one).
- Windows running as administrator can't receive the paste from a non-elevated Mindtalk.
- No vocabulary, recent dictations, statistics, media pausing, onboarding, installer or updates.
