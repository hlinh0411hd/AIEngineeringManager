# AI Employee Manager

AI Employee Manager is a Windows desktop workspace for managing AI-assisted software and content work.

It brings projects, tasks, AI providers, local tools, approvals, test runs, execution history, and generated artifacts into one controlled workflow. Instead of giving an AI system unrestricted access to a folder, the manager keeps work inside explicit project boundaries and requires permissions and review before sensitive actions are performed.

> **Status:** Active source project. The application is designed for Windows and is currently distributed as a self-contained desktop build rather than an installer.

## Why it exists

AI coding and creative tools are powerful, but a typical workflow can become difficult to track:

- Which project was the request for?
- Which provider or model handled it?
- What files were read or proposed for change?
- Which tool generated an artifact?
- Were tests actually run?
- Did a user approve the change or publication?

AI Employee Manager answers those questions by making the work task-centered and reviewable. A task keeps its request, project, access level, execution steps, decisions, outputs, test results, and final review in one place.

## What it can do

### Project and workspace management

- Register multiple local projects.
- Detect common project types such as .NET, Unity, Node.js, Python, and Git repositories.
- Index project file metadata locally with SQLite and FTS5 search.
- Discover project keywords from bounded file and README/manifest scans.
- Match requests to projects using names, aliases, paths, and unambiguous keywords.
- Ask for an explicit project selection when a request is unknown or ambiguous.
- Exclude hidden files, links, dependencies, build outputs, and data folders from discovery scans.

### Task-based AI work

- Create review, planning, coding, research, testing, and other task types.
- Choose the project and access level for each task.
- Use `ReadProject` for inspection and `WriteProject` only when code changes are explicitly allowed.
- Run simple tasks directly or let the manager create a bounded plan for larger goals.
- Track dependencies between steps and resume from persisted checkpoints.
- Pause, stop, rerun, switch provider, or continue a task with a new comment.
- Keep task history, decisions, handoffs, proposals, and final review together.
- Support Manual, SemiAutomatic, and Automatic continuation modes while preserving permission and review gates.

### AI provider routing

The manager can inspect and route work to configured local AI providers and command-line tools. The source includes adapters for providers such as:

- Codex
- Claude Code
- GitHub Copilot
- Gemini
- OpenCode
- Qoder
- Antigravity
- Ollama for qualified local categories

Provider availability is detected from the installed executable and its capabilities. The application does not assume that an installed executable means that the user is authenticated, has quota, or can use every model. Provider, model, access, priority, and write permissions are configured explicitly in **Settings > AI & Models**.

### Safe coding handoff

For providers that return proposed edits, the manager can:

1. Provide a bounded project snapshot when the task has permission to read the project.
2. Receive a structured edit proposal.
3. Check paths, file hashes, project boundaries, excluded locations, and edit sizes.
4. Show the proposal in task history for review.
5. Apply reviewed edits only after explicit user approval.

Codex can use its configured sandbox. Other supported CLI providers generally return proposals rather than writing directly to the project. The manager does not silently grant write access or apply a provider response automatically.

### Testing workspace

- Register project-specific test suites with explicit commands and arguments.
- Run all enabled suites, one selected suite, or suites selected by changed source paths.
- Keep test jobs scoped to the selected project.
- Review command output, retries, cancellation, cached results, and test status.
- Attach testing to an update task and use the latest reviewed edit proposal as the change scope.
- Import and edit `ProjectTestProfile` JSON through the Testing workspace.

Test commands are user-configured. The manager does not install test runners, enable unknown commands, or treat generated code as tested without an actual test run.

### Tool and connector workflows

Tasks can combine AI steps with explicitly configured tools. The current source includes connector support for:

- **Pixel Art Tool** — create, edit, and animate image assets through its configured local backend.
- **TrailerForge** — inspect and export an existing saved video project.
- **ZapBop Music Producer** — inspect and render existing saved projects to WAV through its bridge.
- **Rikerat Marketing** — manage configured media/content workflows through a local backend, including review-gated publishing operations.

Tool connectors are disabled until configured. Depending on the connector, setup may require a local backend, a source folder, a Python environment, a saved project, an endpoint, or a session token. The manager does not install external tools, log in to accounts, repair backend data, or publish content without explicit approval.

Project skills under `.agents/skills` provide bounded instructions and metadata for supported operations. A skill can describe an implemented connector operation, but adding a text file cannot create a new API or grant new permissions.

### Autonomous goals

Large goals can be broken into milestones with acceptance criteria. A goal may include research, implementation, testing, and correction rounds. Progress is persisted in the task, and the run stops when the criteria pass, the configured budget is reached, or user review is required.

Autonomous execution is still bounded by project ownership, permissions, provider limits, tool permissions, test policies, and final review. It is not unrestricted background automation.

### Local virtual office

The desktop UI includes a visual workspace that can show worker and tool activity. It is intended as an ambient view of task state; it does not replace the task history, approvals, or execution logs.

## What makes it different

### Explicit boundaries instead of implicit trust

Project access, provider write access, tool permissions, and publishing approval are separate decisions. A task cannot expand its own access level.

### Reviewable execution

Plans, provider choices, tool inputs, decisions, proposed edits, artifacts, test results, and handoffs are persisted instead of disappearing into a chat transcript.

### Deterministic policy around AI

The core policy and routing layer is implemented in C# and remains responsible for validation, budgets, project ownership, path safety, process control, and state transitions. AI output is treated as untrusted input that must pass application-side validation.

### Local-first storage

The manager stores projects, tasks, settings, checkpoints, indexes, logs, and test records in a local SQLite database. It does not require a hosted AI service for the desktop UI, project indexing, routing policy, or task storage.

### AI and tools are separate capabilities

An AI provider generates or evaluates information. A connector performs a specific tool operation. The task engine coordinates them, but each operation still has its own configuration and permission boundary.

## Quick start

### Requirements

- Windows 10 or later with a compatible desktop environment.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for building from source.
- An installed and authenticated AI provider only if you want to run AI tasks.
- Any external backend, Python environment, saved project, or account required by a connector you want to use.

The application does not install AI CLIs, models, Python packages, test runners, or connector backends automatically.

### Build and run from source

From the repository root:

```bat
Build.bat
```

`Build.bat` restores dependencies, builds the solution in Release mode, runs the executable checks, and creates a self-contained Windows x64 folder at `Release\AIEmployeeManager`.

To launch the newest available local build:

```bat
Run.bat
```

You can also run the application directly after building:

```text
Release\AIEmployeeManager\AIEmployeeManager.exe
```

### Build manually

```powershell
dotnet restore AIEmployeeManager.slnx -m:1 -nr:false
dotnet build AIEmployeeManager.slnx -c Release --no-restore -m:1 -nr:false
dotnet run --project tests/Employee.Checks -c Release --no-build
```

The solution targets .NET 10. The desktop project targets `net10.0-windows` and uses WPF.

### Use the published folder

The Release folder created by `Build.bat` is self-contained for Windows x64. Copy the complete `Release\AIEmployeeManager` folder to another compatible Windows machine and start `AIEmployeeManager.exe`.

## First-use workflow

1. Start the application with `Run.bat` or launch the published executable.
2. Register a local project.
3. Choose **Update file index** when you want to refresh project metadata and keywords.
4. Open **Settings > AI & Models** and inspect available providers.
5. Enable only the providers and models you intend to use.
6. Create a task and select the correct project and access level.
7. Use `ReadProject` for review or research. Select `WriteProject` only for an intentional code-change task.
8. Start the task with **Assign & run**, or save it for later.
9. Review proposed plans, tool inputs, file edits, test results, and decisions.
10. Apply reviewed edits or approve the final result only after checking the output.

For a first run, use a non-production project or a disposable test repository.

## Data and privacy

- The default application data folder is `Data` next to the application root.
- The database contains local project metadata, tasks, settings, checkpoints, logs, indexes, test profiles, and execution history.
- Storage is local and is not encrypted by the application.
- The manager does not store provider credentials in its database. Authentication is handled by the relevant native provider or backend.
- Configure a different data folder from the application settings when needed.
- Do not commit `Data`, database files, credentials, tokens, or generated private artifacts.
- Review provider and connector configuration before enabling network access or publishing actions.

The repository ignores common local data and build output paths, but users should still review their own Git status before publishing a repository.

## Project structure

```text
AIEmployeeManager/
├── src/
│   ├── Employee.Core/              # Domain models, contracts, routing, and policy
│   ├── Employee.Infrastructure/    # SQLite, providers, tools, orchestration, and process control
│   ├── Employee.Desktop/           # WPF application and UI composition
│   └── Employee.Updater/            # Local reviewed-update helper
├── tests/
│   └── Employee.Checks/             # Dependency-light executable acceptance checks
├── connectors/                      # Manager-owned connector bridges
├── .agents/skills/                  # Tool skill metadata and operating guidance
├── docs/                            # Detailed architecture, integration, and workflow documentation
├── Build.bat                        # Restore, build, check, and publish
└── Run.bat                          # Launch the newest local desktop build
```

## Documentation guide

- [Architecture](docs/ARCHITECTURE.md) — application layers, state, process control, storage, and safety boundaries.
- [Tool and API integrations](docs/INTEGRATIONS.md) — provider and connector inventory, configuration, and known limitations.
- [V2 task execution](docs/V2.md) — planning, budgets, approvals, queue behavior, and execution limits.
- [V3–V4 setup and behavior](docs/V3-V4.md) — dashboard, connectors, local updates, and implementation boundaries.
- [V5 AI/tool orchestration](docs/V5.md) — skills, mixed AI/tool plans, and task automation policy.
- [Autonomous goals](docs/AUTONOMOUS-GOALS.md) — milestone planning and acceptance-criteria workflows.
- [Verification](docs/VERIFICATION.md) — verification scope and known test limitations.
- [ZapBop connector](docs/ZAPBOP-CONNECTOR.md) — setup and behavior for the music connector.

Some detailed documents are implementation notes and may describe a particular review date. The source code and current configuration are authoritative when behavior changes.

## Testing and verification

The repository uses a dependency-light executable check project rather than a test framework-heavy setup. The standard verification path is:

```bat
Build.bat
```

Focused checks can also be run after building:

```powershell
dotnet run --project tests/Employee.Checks -- --acceptance
dotnet run --project tests/Employee.Checks -- --project-assignment
dotnet run --project tests/Employee.Checks -- --virtual-office
```

Checks use temporary fixtures where possible and are designed to avoid paid AI inference. Live provider, connector, publishing, and long-running autonomous workflows require the corresponding local services and user configuration; they are not implied by a successful source build.

## Important limitations

- This is a Windows WPF desktop application, not a web application.
- Optional providers and connectors are not bundled services. Their installation, authentication, compatibility, and quotas must be verified separately.
- A successful provider or tool call does not prove that the generated code, artwork, video, audio, or marketing content is high quality.
- The manager does not automatically install software, models, packages, accounts, or credentials.
- Publishing actions require configured backends and explicit review/approval.
- Local SQLite storage is not encrypted by the application.
- The application does not promise crash-atomic multi-file project edits; inspect files and logs after an interrupted write.
- AI-generated output is not a substitute for human review, security review, licensing review, or project-specific testing.

## Contributing

Before opening a pull request:

1. Keep changes within the existing Core, Infrastructure, Desktop, connector, and test boundaries.
2. Do not add credentials, private project data, database files, build output, or generated personal artifacts.
3. Update the relevant documentation when behavior, permissions, provider support, or connector contracts change.
4. Run `Build.bat`, or at minimum restore, build, and run the focused checks relevant to your change.
5. Describe any provider, backend, or runtime behavior that could not be verified locally.

## License

No license file is currently included in this repository. Add a license before treating the project as open source or reusing it in another project.