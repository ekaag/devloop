# DevLoop

A .NET 10 orchestrator for an agentic coding loop:

```
Plan → Codex build → dotnet build + dotnet test → cloud critic → quality gate → PASS, or retry with feedback (max 3)
```

DevLoop runs in two modes:

| Mode | Builder | Reviewer | Needs |
| --- | --- | --- | --- |
| `Mock` (default) | `MockBuilderAgent` writes a demo artifact | `CriticAgent` over `MockModelClient` | .NET 10 SDK only |
| `Codex` | `CodexBuilderAgent` runs `codex exec` in a dedicated Git workspace | `CloudCriticAgent` over the OpenAI Responses API | Codex CLI, Git, an OpenAI API key |

## Prerequisites (Windows PowerShell)

```powershell
dotnet --list-sdks            # needs a 10.0.x SDK (or use .\.dotnet\dotnet.exe from this repository)
git --version
codex --version               # Codex CLI, signed in with `codex login` (Codex mode only)
dotnet restore devloop.sln    # restore is an explicit, network-capable operator step
dotnet build devloop.sln --no-restore
dotnet test devloop.sln --no-restore
```

## Mock mode (no AI calls)

```powershell
$env:DEVLOOP_Workspace = (Get-Location).Path
dotnet run --project devloop.Orchestrator --no-build -- "Implement a greeting feature"
```

The mock writes an incomplete artifact on iteration 1, the mock critic rejects it, and iteration 2 succeeds.
`.\devloop-startup.ps1` does the same using the repository-local SDK.

## Codex mode

### 1. Create a dedicated workspace

The builder only works in a Git repository root that is separate from DevLoop, has at least one commit,
and has no uncommitted changes. For the bundled example:

```powershell
.\examples\New-SampleWorkspace.ps1 -Destination C:\code\devloop-sample
```

This copies [examples/sample-target](examples/sample-target), commits a baseline, and runs `dotnet restore`
once. The loop itself never restores packages, and Codex has no network access.

### 2. Store the API key in user secrets

```powershell
dotnet user-secrets set "Critic:ApiKey" "<your OpenAI API key>" --project devloop.Orchestrator
dotnet user-secrets list --project devloop.Orchestrator
```

The key is read only by the critic. It is never written to artifacts, never passed to Codex, and never passed
to build or test processes.

### 3. Configure

Settings come from `devloop.Orchestrator/appsettings.json`, then user secrets, then `DEVLOOP_` environment
variables (later sources win). Nested keys use `__` in environment variables.
[appsettings.Codex.example.json](devloop.Orchestrator/appsettings.Codex.example.json) shows a complete Codex configuration.

```powershell
# The npm "codex" command is a .cmd/.ps1 shim; DevLoop requires the native executable.
$codexExe = Get-ChildItem "$env:APPDATA\npm\node_modules\@openai\codex" -Recurse -Filter codex.exe | Select-Object -First 1 -ExpandProperty FullName

$env:DEVLOOP_Mode = 'Codex'
$env:DEVLOOP_Workspace = 'C:\code\devloop-sample'
$env:DEVLOOP_Solution = 'SampleTarget.slnx'
$env:DEVLOOP_ArtifactDirectory = 'C:\code\devloop-runs'      # optional; keeps run artifacts out of the workspace
$env:DEVLOOP_Codex__Executable = $codexExe
$env:DEVLOOP_Critic__Model = 'gpt-5'                        # any Responses API model that supports structured outputs

dotnet run --project devloop.Orchestrator --no-build -- --check --task examples\tasks\slugify.json
```

`--check` validates the configuration and prints it (without the key). It never starts Codex or calls OpenAI.

### 4. Run

Running the loop uses Codex and OpenAI API credits.

```powershell
dotnet run --project devloop.Orchestrator --no-build -- --task examples\tasks\slugify.json
```

Exit codes: `0` succeeded, `1` failed or invalid configuration, `2` cancelled (Ctrl+C), `3` run timeout.

### Task files

Codex mode needs a task file with explicit acceptance criteria; the model does not invent them.
All `acceptanceCriteria` are mandatory, and `optionalCriteria` never block success.
See [examples/tasks/slugify.json](examples/tasks/slugify.json):

```json
{
  "request": "In the TextTools library ... add TextTools.Slugify(string value) ...",
  "steps": ["..."],
  "acceptanceCriteria": ["...", "...", "..."],
  "optionalCriteria": ["Slugify has an XML documentation comment."]
}
```

## Configuration reference

| Key | Default | Meaning |
| --- | --- | --- |
| `Mode` | `Mock` | `Mock` or `Codex` |
| `Workspace` | required | Existing directory; in Codex mode, the root of a dedicated Git repository |
| `Solution` | `devloop.sln` | Solution built and tested, relative to the workspace |
| `MaxIterations` | `3` | 1–3 |
| `CommandTimeoutSeconds` | `120` | Per `dotnet build` / `dotnet test` |
| `RunTimeoutSeconds` | `3600` | Whole run |
| `DotnetExecutable`, `GitExecutable` | `dotnet`, `git` | Trusted executables or absolute paths |
| `ArtifactDirectory` | `<workspace>/.devloop/runs` | Run output; relative paths resolve against the workspace |
| `MockFailures` | `1` | Mock mode only |
| `Codex:Executable` | `codex` | Native `codex.exe` path on Windows (`.cmd`/`.ps1` shims are rejected) |
| `Codex:Model` | Codex default | Optional `--model` |
| `Codex:TimeoutSeconds` | `900` | Per iteration |
| `Codex:IgnoreUserConfig` | `true` | Ignore `~/.codex/config.toml` (sign-in still works) so a user profile cannot widen the sandbox |
| `Codex:RequireCleanWorkspace` | `true` | Refuse to start with uncommitted changes |
| `Codex:PassEnvironment` | `[]` | Extra variables for the Codex process (for example `CODEX_API_KEY`) |
| `Codex:ProtectedPaths` | `.git/ .github/ .devloop/ .codex/ .vscode/ nuget.config global.json` | Builder may not change these |
| `Critic:Model` | required | Responses API model |
| `Critic:ApiKey` | required | User secrets or `DEVLOOP_Critic__ApiKey`, never in a file in the repository |
| `Critic:Endpoint` | OpenAI | Optional alternate endpoint |
| `Critic:TimeoutSeconds` | `180` | HTTP timeout; the SDK retries transient failures |
| `Critic:MaxOutputTokens` | `4000` | Review response limit |
| `Critic:MaxDiffChars` / `MaxContextChars` / `MaxFileContextChars` / `MaxCheckOutputChars` | 60k / 40k / 15k / 6k | Review payload limits |

## How an iteration works

1. **Preflight (iteration 1):** the workspace must be a Git repository root with a commit, must not contain
   DevLoop itself, and must be clean. Its `HEAD` becomes the baseline.
2. **Codex build:** `codex exec --cd <workspace> --sandbox workspace-write --ephemeral --json --output-schema … -o … -`
   with `approval_policy="never"`, `sandbox_workspace_write.network_access=false` and
   `shell_environment_policy.inherit="core"`. The prompt goes to stdin, not the command line. It contains the task,
   acceptance criteria, iteration number and the previous iteration's feedback. Codex returns a schema-checked
   completion report (`completed` / `partial` / `blocked` with per-criterion evidence).
   - A zero exit code is *process* success. *Task* success also requires a `completed` report covering every
     criterion and a non-empty Git diff.
   - DevLoop takes the changes from Git (`status`, `diff <baseline>`, untracked files), not from Codex's claims.
3. **Policy check:** changes are violations if `HEAD` moved (a commit, reset or branch switch), a protected path
   changed, a credential-like file appeared, a path escapes the workspace or is a reparse point, or a credential
   pattern appears in the added lines. A violation stops the run without executing the code.
4. **Deterministic checks:** `dotnet build <solution> --no-restore` and `dotnet test <solution> --no-restore`, with
   timeouts and a sanitized environment.
5. **Cloud critic (only when steps 2–4 passed):** a Responses API call with a strict JSON schema
   (`decision`, `blocking_findings`, `nonblocking_recommendations`, `builder_feedback`, per-criterion `criteria`).
6. **Quality gate:** success requires all of the following. A model PASS never overrides a deterministic failure.
   - Codex succeeded.
   - Build and tests passed without timeout.
   - The review is valid and approves with no blocking findings.
   - Every mandatory criterion is marked met with evidence.
   - There are no policy violations.
7. **Retry or stop:** feedback (gate failures, build/test output, reviewer guidance) goes to the next iteration. The
   loop stops early on a policy violation, a builder failure that retrying cannot fix, a failed review, or an
   unchanged diff (no progress).

## Reviewer safeguards

- The instructions say that the diff, source, tool output and earlier findings are untrusted data, and that
  embedded instructions are themselves findings. The model's output only becomes feedback text; DevLoop never
  executes it.
- Unparseable, incomplete, refused or partial reviews, and API errors, count as failures (`ReviewFailed`), never passes.
- Payloads contain only the task, the bounded diff, changed files (credential-like files omitted), and the tails of
  the check output. They contain no environment variables and no unrelated files. Known secrets and credential
  patterns are redacted. Responses are created with `store: false`.

## Artifacts

Each run creates `<ArtifactDirectory>/<timestamp>-<id>/` containing:

| File | Contents |
| --- | --- |
| `plan.json` | Request, steps, criteria |
| `iteration-N.json` | Builder result (process, report, changed files, diff), checks, review, gate result, feedback received |
| `iteration-N/codex-prompt.md`, `codex-events.jsonl`, `codex-stderr.txt`, `codex-last-message.json`, `changes.diff` | Raw Codex exchange |
| `iteration-N/critic-request.json`, `critic-response.json` | Exactly what was sent to and returned by the reviewer (redacted) |
| `outcome.json` | Final status, stop reason, history, remaining issues, token usage |
| `report.md` | Human-readable report |

Token usage comes from Codex `turn.completed` events and from Responses API `usage`. It is logged and totalled in the reports.

## Security boundaries and limits

- Codex's OS sandbox (`workspace-write`, no network) is the primary file and network boundary. DevLoop never uses
  `danger-full-access`, `--dangerously-bypass-approvals-and-sandbox` or `--add-dir`. The Git checks after each build
  catch violations inside the repository. They cannot see writes outside it, which only the sandbox prevents.
- Codex receives an allowlisted environment, and the commands it runs inherit only the core variables. Build and test
  processes get the current environment minus anything that looks like a credential or `DEVLOOP_` setting.
- Build and test still execute builder-written code on this machine. Use a workspace you are willing to expose, and
  consider a VM or container for untrusted tasks.
- Git is read-only (status, diff, rev-parse) with fsmonitor and external diff drivers disabled. DevLoop never
  commits; review and commit the result yourself.
- Run one loop per workspace at a time. No Azure resources or deployment configuration are included.

## Tests

`dotnet test devloop.sln --no-restore` runs both suites. They need Git, and they make no AI or network calls:

- `LoopTests` covers Milestone 1.
- `CodexLoopTests` covers Milestone 2: success, test failure then retry, reviewer rejection then repair, maximum
  retries, model failure, malformed and incomplete reviews, Codex process failure, cancellation and timeouts,
  empty or missing diff, and boundary and credential violations. Codex and OpenAI are fakes; Git runs against
  throwaway repositories.
