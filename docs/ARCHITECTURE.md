# Flux architecture

## Component map

```text
MainWindow (WPF command palette)
  ├─ DeterministicCommandRouter
  │    ├─ ApplicationCatalog
  │    └─ FileSearchService
  ├─ Direct Windows actions
  │    ├─ WindowsProcessService
  │    └─ WindowsSystemInfoService
  └─ FluxAgent (only for semantic/multi-step requests)
       ├─ IAiProvider
       │    └─ AiProviderRouter → local compatible endpoint by default
       └─ IToolRegistry
            └─ typed IFluxTool implementations + permission metadata
```

Supporting services own global hotkey registration, tray lifecycle, startup registration, local JSON settings/history, and local logs.

## Routing flow

1. Text changes are debounced for 140 ms.
2. Known folders, explicit process commands, system queries, app names, and file names are routed deterministically.
3. Only semantic questions or complex/multi-step actions produce an AI result.
4. The model receives the user request, a small relevant context, and tool schemas—never unrestricted computer access or a full system dump.
5. Level 0/1 model tools can execute and feed results back to the model for up to six turns.
6. Level 2/3 tool requests are returned to the UI as pending actions. The host executes them only after the user clicks the confirmation button.

## Safety invariants

- Tool implementations, not prompts, enforce permission levels and path/process checks.
- Unknown tools are refused.
- Protected Windows processes, session-zero processes, Explorer, and Flux itself cannot be terminated.
- Arbitrary PowerShell is Level 3 and never runs automatically.
- Local folder creation requires a fully qualified path under the current user's profile.
- Cloud credentials are environment-only; settings contain no secret field.
- Local mode never falls back to cloud. Automatic mode is an explicit user choice.

## Storage

Flux stores only small local utility data under `%LOCALAPPDATA%\Flux`:

- `settings.json` — provider endpoints/model names, hotkey, startup and retention preferences
- `history.json` — recent command text, capped by the configured retention count
- `Logs\flux-YYYY-MM-DD.log` — operational errors and catalog refresh information

Tool output and system snapshots are not added to command history.

## Extension points

Add a tool by implementing `IFluxTool` and registering it in `ToolRegistry`. Every tool must declare its name, description, JSON parameter schema, permission level, and executor. Add providers behind `IAiProvider`; routing and UI do not depend on a vendor SDK.

