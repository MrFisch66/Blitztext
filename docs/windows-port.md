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
- Left-click the pill opens the slim **shortcut editor** (the most common task). Right-click the
  pill for a menu: *Tastenkürzel* (shortcut editor), *Einstellungen* (full settings window), and
  *Beenden*. The tray icon offers the same entries.
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
  - `Ctrl + Alt + D`: Blitztext `$%&!`
  - `Ctrl + Alt + E`: Blitztext `:)`
- API keys are stored in Windows Credential Manager.
- Settings live under `%LOCALAPPDATA%\Blitztext\settings.json`.
- Temporary recordings are WAV files under `%LOCALAPPDATA%\Blitztext\cache`.

## Local Transcription

The Windows local path uses `whisper.cpp` instead of WhisperKit/CoreML. Models are downloaded on demand to `%LOCALAPPDATA%\Blitztext\models\whispercpp`.

The app can install the official Windows `whisper.cpp` runtime from the latest `ggml-org/whisper.cpp` GitHub release into `%LOCALAPPDATA%\Blitztext\tools\whispercpp`. If you prefer a custom runtime, set `BLITZTEXT_WHISPER_CPP` to an existing `whisper-cli.exe`.

## Store Packaging

The MSIX scaffold lives in `src/Blitztext.Packaging`. Build it from a Visual Studio Developer PowerShell with MSIX Packaging Tools:

```powershell
.\build-windows.ps1 -Configuration Release -Package
```

Before Partner Center submission, replace `Package.appxmanifest` identity values with the reserved Store identity.
