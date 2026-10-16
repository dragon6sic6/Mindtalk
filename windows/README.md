# Mindtalk for Windows

A Windows port of Mindtalk: hold a key, speak, let go — your words are typed where
your cursor is. Everything runs on-device.

**Status: early.** The speech and text pipeline works from the command line; the tray
app (hotkey, recording, paste) is next.

## Stack

- C# / .NET 9
- [ONNX Runtime](https://onnxruntime.ai) running Parakeet TDT 0.6B v3 (the Mac app uses Core ML conversions, which don't run on Windows)
- WinUI 3 for the tray app and settings *(not started)*

| Project | What |
|---|---|
| `src/Mindtalk.Core` | Text cleanup, model catalog and download (SHA-256 pinned), the Parakeet engine |
| `src/Mindtalk.Cli` | Self-tests: `--clean`, `--install`, `--transcribe` |
| `tests/Mindtalk.Core.Tests` | xunit tests |

## Speech models

Downloaded once to `%LOCALAPPDATA%\Mindtalk\Models`, every file checked against a pinned SHA-256.

| | Model | Source | Size |
|---|---|---|---|
| Swedish | Klang Pianissimo (int8) | [KlangAI/pianissimo-sv-onnx](https://huggingface.co/KlangAI/pianissimo-sv-onnx) | ~660 MB |
| Multilingual | Parakeet Ultra (int8) | [Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx](https://huggingface.co/Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx) (community export) | ~670 MB |

## Develop

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
cd windows
dotnet test
dotnet run --project src/Mindtalk.Cli -- --clean "Eh, vi ses på på fredag ny rad tack"
dotnet run --project src/Mindtalk.Cli -- --install --model swedish
dotnet run --project src/Mindtalk.Cli -- --transcribe sample.wav --model swedish --clean
```

## Mac → Windows map

| Mac | Windows |
|---|---|
| Core ML via FluidAudio | ONNX Runtime (`ParakeetEngine`) |
| `TextCleanup.swift` | `TextCleanup.cs` (ported, same tests) |
| Core Graphics event tap (global key) | low-level keyboard hook (`WH_KEYBOARD_LL`) — *todo* |
| Audio Queue | WASAPI capture — *todo* |
| Paste via Accessibility | clipboard + `SendInput` Ctrl+V — *todo* |
| Apple Intelligence polish | none on Windows for now |
| Sparkle | *todo* |
