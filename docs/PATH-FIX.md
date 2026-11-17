# Codex detection fix — 2026-11-01

The original GUI resolver searched only its process PATH. Codex Desktop supplies its bundled CLI through a versioned directory under `%LOCALAPPDATA%\OpenAI\Codex\bin`, which need not appear in the environment inherited by Explorer-launched applications. Consequently Found=false was not proof that Codex was absent.

Resolver now checks process PATH, persisted user/system PATH and recognized npm launchers. Codex additionally checks the known Desktop bin location one directory level down, ignores incomplete version folders and prefers the most recently modified existing executable. Explicit executable configuration remains authoritative; an invalid configured path never silently selects another installation. Empty/relative PATH entries are ignored, avoiding accidental current-directory lookup. No installation or PATH mutation is performed.

The provider detail now displays the actual resolved executable. Headless flags are still inspected before Ready=true; discovery alone does not imply authentication or known quota.

Verification in `D:\Projects\AIEmployeeManager`: Debug build, zero warnings/errors; 24 checks passed including missing-PATH, version selection and configuration precedence. A real detection process with its process PATH replaced by a nonexistent folder reported Installed=true and CanExecute=true using the bundled Codex executable. No AI inference, Release build or archive was invoked.

Reopen `Run.bat` from this project folder and select **Detect CLI capabilities & local models**. Normal saved user/project/provider data is preserved. Auth/Usage may remain Unknown independently of installation detection.
