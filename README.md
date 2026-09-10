# Flux

Flux is a native Windows search and command utility. Press `Alt + Space`, type what you want, and Flux uses the fastest safe path:

- app, file, folder, and explicit system commands are handled locally and deterministically;
- requests that need interpretation are sent to a local model;
- every model capability is exposed as a typed Flux tool with a fixed permission level;
- disruptive and destructive actions stop at an in-app confirmation boundary.

Flux is an actual WPF desktop application, not a browser shell. It lives in the system tray, can start with Windows, discovers Start Menu and registered applications, searches user files, keeps local command history, and provides native process/system inspection.

Flux can also check GitHub Releases asynchronously whenever it launches. The check never delays the launcher: failures stay silent, while a newer stable version appears as a tray notification and a persistent download item in the tray menu.

## Run from source

Requirements: Windows 10/11 and the .NET 10 SDK.

```powershell
dotnet build Flux.slnx
dotnet run --project src/Flux.Windows/Flux.Windows.csproj
```

The default global shortcut is `Alt + Space`. It can be changed in Settings.

## Local AI (default)

Flux defaults to Ollama at `http://localhost:11434` and the `qwen3:8b` model. Install and start Ollama, then pull a tool-capable model:

```powershell
ollama pull qwen3:8b
```

Open Flux Settings, select **Local**, choose the model, and use **Test local connection**. Flux does not silently fall back to the cloud in Local mode.

The provider uses Ollama's OpenAI-compatible `/v1/chat/completions` endpoint and supports iterative tool calls. Any compatible local server can be used by changing the endpoint and model.

Cloud mode is optional. It reads `OPENAI_API_KEY` from the environment; API keys are never written to Flux settings or this repository.

## Safety levels

| Level | Policy | Examples |
|---|---|---|
| 0 | Automatic, read-only | search, process list, system stats |
| 1 | Automatic, reversible | launch, open, create user folder |
| 2 | Confirmation required | close, terminate, restart process |
| 3 | Explicit destructive confirmation | unrestricted PowerShell |

System/session processes and a protected Windows process list are refused even after an AI request. Automatic folder creation is restricted to the current user profile.

## Build a distributable package

```powershell
.\scripts\Build-Release.ps1
```

This runs the build and safety tests, publishes a self-contained `win-x64` app, creates `artifacts/Flux-win-x64.zip`, and writes its SHA-256 hash. Use `-FrameworkDependent` for a smaller package that requires the .NET 10 Desktop Runtime.

`packaging/Flux.iss` is an Inno Setup definition for producing a per-user installer from the published folder. Compile it after running the release script. The portable zip works without an installer.

To connect a release build to a public GitHub Releases feed, provide the repository when packaging:

```powershell
.\scripts\Build-Release.ps1 -GitHubRepository owner/repository
```

You can alternatively set `FLUX_UPDATE_REPOSITORY=owner/repository`. Create stable tags such as `v0.1.1` and attach `Flux-win-x64.zip` to each GitHub Release. Private repositories are not supported because Flux deliberately does not embed a GitHub access token.

The included GitHub Actions release workflow does this automatically. After pushing the project, create and push a three-part version tag:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The Windows workflow builds and tests Flux, embeds the actual GitHub repository and tag version, and publishes the portable ZIP as the release asset.

## Project layout

- `src/Flux.Core` — routing, search scoring, provider/tool contracts, permission model, agent safety gate
- `src/Flux.Windows` — WPF UI, tray/hotkey/startup integration, Windows discovery and system services, providers and tools
- `tests/Flux.Core.Tests` — dependency-free behavioral test runner
- `docs/ARCHITECTURE.md` — component boundaries and execution flows
- `scripts/Build-Release.ps1` — verified Windows release packaging
