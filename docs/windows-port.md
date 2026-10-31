# Windows Port

Blitztext now has a native Windows port scaffold next to the existing macOS app. The Windows app is a .NET 8 WPF tray application with the same workflow model as the macOS preview.

## Build

```powershell
dotnet build .\Blitztext.sln -c Release
dotnet test .\Blitztext.sln -c Release
.\build-windows.ps1 -Configuration Release
```

The publish output is written to `artifacts/windows/win-x64`.

## Runtime Behavior

- Blitztext runs in the background. The only permanently visible part is a small floating
  "pill" docked at the bottom-center of the screen (similar to Wispr Flow). It does not take
  focus and stays out of Alt+Tab, so dictation lands in whatever app you were typing in.
- The pill reflects state: neutral when idle, **green while recording**, amber while the text
  is being transcribed/rewritten, and a short red flash on errors. While recording it shows a
  small live microphone level.
- Left-click the pill shows the **last dictated text** in a small editable window with a
  *copy to clipboard* button — a fallback for when the cursor was in the wrong place. Right-click
  the pill for a menu: *Tastenkürzel* (shortcut editor), *Einstellungen* (full settings window),
  and *Beenden*. The tray icon offers the same entries.
- Closing any window hides it; the background process keeps running.
- Global hotkeys are handled by a low-level keyboard hook (`WH_KEYBOARD_LL`) instead of
  `RegisterHotKey`, which makes **modifier-only shortcuts** such as `Ctrl + Win` possible and
  gives reliable push-to-talk (real key-release instead of polling).
- Hotkeys are configured in the dedicated shortcut editor — one row per workflow with the
  shortcut field and an enable toggle, no manual start/stop. Click a field and press the
  combination; it is applied live and shown in the field immediately. Modifier-only
  combinations need at least two modifiers; `Escape` restores the default. While the editor is
  open the global hotkeys are paused, so rebinding to an in-use combination cannot trigger a
  recording. Defaults:
  - `Ctrl + Alt + Space`: Blitztext
  - `Ctrl + Alt + Shift + Space`: Blitztext Lokal
  - `Ctrl + Alt + T`: Blitztext+
- API keys are stored in Windows Credential Manager.
- Settings live under `%LOCALAPPDATA%\Blitztext\settings.json`.
- The dictated text is typed into the target via `SendInput`. The user's clipboard is snapshotted
  before the paste and restored afterwards, so dictation never clobbers the clipboard.
- Temporary recordings are WAV files under `%LOCALAPPDATA%\Blitztext\cache`.

## Cloud Transcription

- Files are transcribed with OpenAI `gpt-transcribe`; custom terms are sent as `keywords`.
- With *Live-Transkription während der Aufnahme* (default on), audio is streamed while recording to
  `gpt-live-transcribe` over the Realtime API (WebSocket, 24 kHz PCM, resampled from the 16 kHz
  capture). Releasing the hotkey only commits the buffer, so the text arrives about half a second
  later. If the live session fails or does not finish within 5 s, the recorded WAV is uploaded as a
  file instead (logged as an error in `blitztext.log`).

## Local Transcription

- **Parakeet TDT 0.6B v3** (default, recommended) runs in-process via sherpa-onnx (int8 ONNX, CPU).
  The model stays loaded, so a dictation only pays for decoding (~0.4 s for 10 s of speech on a
  Ryzen 9 7900X). It is downloaded on demand (~670 MB) to
  `%LOCALAPPDATA%\Blitztext\models\parakeet`. Custom terms are not applied to Parakeet.
- **Whisper** models (Small, Medium, Large v3 Turbo) use `whisper.cpp`. Models are downloaded on
  demand to `%LOCALAPPDATA%\Blitztext\models\whispercpp`. The app installs the official Windows
  CPU runtime from the newest `ggml-org/whisper.cpp` build release (`bNNNN`; the versioned `v1.9.x`
  releases no longer carry binaries) into `%LOCALAPPDATA%\Blitztext\tools\whispercpp`. If you
  prefer a custom runtime, set `BLITZTEXT_WHISPER_CPP` to an existing `whisper-cli.exe`.

## Store Packaging

The MSIX scaffold lives in `src/Blitztext.Packaging`. Build it from a Visual Studio Developer PowerShell with MSIX Packaging Tools:

```powershell
.\build-windows.ps1 -Configuration Release -Package
```

Before Partner Center submission, replace `Package.appxmanifest` identity values with the reserved Store identity.
