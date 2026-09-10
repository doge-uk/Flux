# Flux

Flux is a native Windows search and command utility. Press `Alt + Space`, type what you want, and Flux uses the fastest safe path:

- app, file, folder, and explicit system commands are handled locally and deterministically;
- requests that need interpretation are sent to a local model;
- every model capability is exposed as a typed Flux tool with a fixed permission level;
- disruptive and destructive actions stop at an in-app confirmation boundary.

Flux is an actual WPF desktop application, not a browser shell. It lives in the system tray, can start with Windows, discovers Start Menu and registered applications, searches user files, keeps local command history, and provides native process/system inspection.

Flux checks GitHub Releases asynchronously whenever it launches. The check never delays the launcher. A newer stable version appears in the tray, where one click downloads the versioned installer, verifies its size and GitHub SHA-256 digest, launches the silent per-user upgrade, and exits the old Flux process. Failed verification never opens the installer and leaves the current installation unchanged.

## Run from source

Requirements: Windows 10/11 and the .NET 10 SDK.

```powershell
dotnet build Flux.slnx
dotnet run --project src/Flux.Windows/Flux.Windows.csproj
```

The default global shortcut is `Alt + Space`. It can be changed in Settings.

## Local AI (default)

Flux defaults to Ollama at `http://localhost:11434` with `qwen3.5:2b` as its fast command model and `qwen3.5:9b` as its optional larger model. Install and start Ollama, then pull the models:

```powershell
ollama pull qwen3.5:2b
ollama pull qwen3.5:9b
```

Open Flux Settings, select **Local**, choose the model, and use **Test local connection**. Flux does not silently fall back to the cloud in Local mode.

The fast model handles ordinary commands and can privately route genuinely complex requests to the configured larger model. The provider uses Ollama's OpenAI-compatible `/v1/chat/completions` endpoint, streams normal text responses, and supports iterative tool calls. Any compatible local server can be used by changing the endpoint and models.

Cloud mode is optional. It reads `OPENAI_API_KEY` from the environment; API keys are never written to Flux settings or this repository.

## Safety levels

| Level | Policy | Examples |
|---|---|---|
| 0 | Automatic, read-only | search, process list, system stats |
| 1 | Automatic, reversible | launch, open, create user folder |
| 2 | Confirmation required by default | close, terminate, restart process |
| 3 | Explicit destructive confirmation | unrestricted PowerShell |

The close-confirmation setting may be disabled for normal application-close commands only. Force termination, restart, and destructive tools remain gated. System/session processes and a protected Windows process list are refused even after an AI request. Automatic folder creation is restricted to the current user profile.

## Build a distributable package

```powershell
.\scripts\Build-Release.ps1
```

This runs the build and safety tests, publishes a self-contained `win-x64` app, creates `artifacts/Flux-win-x64.zip`, and writes its SHA-256 hash. When Inno Setup 6 is installed, it also creates a versioned per-user installer under `artifacts/installer`. Use `-RequireInstaller` in release automation and `-FrameworkDependent` for a smaller package that requires the .NET 10 Desktop Runtime.

`packaging/Flux.iss` defines an upgradeable per-user installation under `%LOCALAPPDATA%\Programs\Flux`, including Start Menu and optional desktop/startup entries. Settings and history remain in `%LOCALAPPDATA%\Flux` across upgrades. The portable ZIP remains available for users who do not want an installation.

To connect a release build to a public GitHub Releases feed, provide the repository when packaging:

```powershell
.\scripts\Build-Release.ps1 -GitHubRepository owner/repository
```

You can alternatively set `FLUX_UPDATE_REPOSITORY=owner/repository`. Stable releases must include both `Flux-win-x64.zip` and the exact versioned setup executable produced by the build. Private repositories are not supported because Flux deliberately does not embed a GitHub access token.

The included GitHub Actions release workflow does this automatically. After pushing the project, create and push a three-part version tag:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The Windows workflow builds and tests Flux, embeds the actual GitHub repository and tag version, compiles the installer, and publishes both the setup executable and portable ZIP as release assets.

## Project layout

- `src/Flux.Core` — routing, search scoring, provider/tool contracts, permission model, agent safety gate
- `src/Flux.Windows` — WPF UI, tray/hotkey/startup integration, Windows discovery and system services, providers and tools
- `tests/Flux.Core.Tests` — dependency-free behavioral test runner
- `docs/ARCHITECTURE.md` — component boundaries and execution flows
- `scripts/Build-Release.ps1` — verified Windows release packaging
