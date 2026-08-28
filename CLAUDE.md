# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CoachingIA reads the JSONL transcripts Claude Code leaves under `~/.claude/projects/` (plus, optionally, real-time HTTP hooks) and turns them into coaching signals: session/task segmentation, per-task signals (context pressure, cache ratio, tool failure rate, etc.), weekly reviews (`bilan`), and multi-month retrospectives (`retro`). It is entirely local: no telemetry leaves the machine except spans sent to a local Phoenix (Arize) instance over OTLP. The project is French-first — code comments, CLI output, and docs are in French; keep new user-facing strings and comments in French to match.

The system currently **observes and does not coach yet** — no LLM judge, no persistence beyond files, no maturity scoring loop. See `docs/architecture-v0.*.html` for the roadmap and the "paliers" (levels 1-5) model referenced throughout the code (`coaching.level` span attribute, `SignalSpec.Level`).

Read `README.md` first — it is long, in French, and is the actual product spec (CLI commands, the weekly review's design rules, the lens/vocabulary system, privacy guarantees). This file only adds what a coding agent needs that isn't already there.

## Build, test, run

Requires .NET SDK 10 (`dotnet --version`). Docker is only needed for the OpenTelemetry/Phoenix path, not for the CLI.

```powershell
dotnet build                                        # whole solution (CoachingIA.slnx)
dotnet run --project src/CoachingIA.Cli -- <command> # see below
dotnet run --project tests/CoachingIA.Harness.Tests  # run all tests
```

There is no test framework (no xUnit/NUnit) — `tests/CoachingIA.Harness.Tests` is a plain console app. `Program.cs` builds a local `Check(condition, label)` closure and calls a sequence of static `*Tests.Run(Check, ...)` methods, one per suite (`Transcripts.cs`, `Usage.cs`, `Lenses.cs`, `Variants.cs`, `Review.cs`, `Retro.cs`, `Archive.cs`, plus inline hook/span checks at the top of `Program.cs`). It exits 1 if any check failed and prints `FAIL <label>` for each. There is no way to run a single named test other than commenting out the suites you don't want in `Program.cs` — this is expected, not a gap to fix.

The first suite in `Program.cs` uses a real `ActivityListener` (not a mock) to capture spans emitted by `SpanFactory`, verifying trace shape (parent/child span IDs, span kind, durations, error status) without needing Phoenix running.

`TreatWarningsAsErrors` is on solution-wide (`Directory.Build.props`) — a build with warnings fails.

CLI commands (run via `dotnet run --project src/CoachingIA.Cli --`): `probe`, `analyze`, `segment`, `usage`, `team`, `lens`, `defi`, `moment`, `bilan`, `retro`, `web`. Run with no arguments for the full help text with options. `probe --root <dir>` is the fastest smoke check that transcript parsing still works against real data.

To validate the hooks → harness → Phoenix pipe end-to-end without touching real Claude Code sessions: start Phoenix (`docker compose -f docker/docker-compose.yml up -d`), start the harness (`dotnet run --project src/CoachingIA.Harness`), then `pwsh scripts/smoke-test.ps1` — it replays a synthetic session and checks the harness reports 0 open spans afterward.

## Architecture

Three .NET projects plus a French-only naming convention (`CoachingIA.Harness` = the web host, not "Harness" in the generic sense):

- **`src/CoachingIA.Harness.Core`** — all logic, zero external NuGet dependency (only `Microsoft.AspNetCore.App` framework reference, for `Results`/hosting types shared with the web host). This is deliberate: the core must build and test fully offline. Two areas:
  - Root + `Transcripts/`: the batch/replay path. `TranscriptReader` streams JSONL tolerantly (unreadable lines are counted, never fatal) → `TranscriptRecord` → `SessionBuilder` assembles `ConversationModel` (sessions/turns/tool calls) → `TaskSegmenter` splits turns into tasks using a lexical+temporal heuristic (explicitly called out in README as "a bet," logs its own decisions, expect to retune it) → `SignalExtractor` computes the per-task signals. `TranscriptIngestor` replays a `ConversationModel` into OpenTelemetry spans at their *original* timestamps (not replay time). `TranscriptProbe` produces the anonymous, shareable format-diagnostic report used by `probe`.
  - `Coaching/`: everything downstream of signals. `SignalSpec` defines each signal's target/direction/level. `Lens` + `LensVariants` implement the vocabulary-skin system (see README "La lentille" section) — a lens changes wording only, never measurements/thresholds, and always falls back to the neutral lens key-by-key. `WeeklyReview`/`ReviewRenderer`/`HtmlReviewRenderer` build `bilan`; `Retrospective`/`HtmlRetrospectiveRenderer` build `retro`; `ReviewArchive` handles the "don't clobber, archive by mtime, no-op on identical regeneration" file-write rule described in README. `PromptRubric`/`PromptCritic` implement the seven-criteria prompt critique, with `IPromptCritic` swappable between the offline heuristic and a `claude -p` judge (`--juge`).
- **`src/CoachingIA.Harness`** — the real-time path only: an ASP.NET minimal-API host mapping `POST /hooks/{eventName}` (Claude Code hook payloads → `SpanFactory` → OpenTelemetry spans exported via OTLP/gRPC to Phoenix), `POST /ingest` (triggers the batch replay path, so both collection routes converge on the same span model), `GET /health`, `GET /status`. `Routes.Map` in `Program.cs` is the explicit URL-segment → canonical hook-name table — don't try to derive it automatically, some names diverge on purpose (e.g. `post-tool-fail` → `PostToolUseFailure`). **Every hook handler must return fast and return 200**: a hook blocks the user's turn until it responds, so a coaching-side failure must never fail someone's Claude Code session. `IdleSweeper` force-closes spans whose session was never cleanly ended (editor killed, machine slept).
- **`src/CoachingIA.Cli`** — the `coachingia` executable (`AssemblyName` is lowercase, unlike the project name). `Program.cs` is a straight top-level-statements script with one `int Xxx()` local function per subcommand; `WebConsole.cs` implements the `web` subcommand's local-only HTTP console (binds `127.0.0.1` only, allow-lists commands/options — arguments are passed as an array so there's no shell string to inject, served file paths are collapsed to a filename re-joined under the output directory, and a session token minted at startup is checked on every call). When touching `web`, preserve all three of those guarantees rather than just making the feature work.

Span/attribute conventions live in `OpenInference.cs`: `OI` holds OpenInference semantic-convention attribute names (Phoenix-native, e.g. `openinference.span.kind`, `input.value`) and `OI.Kind` (`AGENT`/`CHAIN`/`TOOL`/`LLM`/...); `Coach` holds project-specific additions layered on top (`coaching.level`, `coaching.signal`, `coaching.outcome`, `coaching.source` distinguishing transcript-replay spans from real-time hook spans). Both namespaces are additive — never repurpose an OpenInference attribute name for coaching-specific data.

`lenses/*.json` are data, not code — `neutre`, `starcraft2`, `echecs`. Each lens can be further split by `race`/side (see `starcraft2.json`'s per-race scenes). `skills/coach-starcraft2.md` documents the register/tone rules for writing new StarCraft II scenes (lived scenes, not unit name-dropping). If you add scenes or a new lens, keep to the three code-enforced rules from the README: the factual sentence always precedes the image and stays true if the image is deleted; a lens never changes a measurement or threshold; the neutral lens must stay complete since every other lens falls back to it key-by-key.

`_a_supprimer/` is a to-be-deleted holding area (old tarballs) — do not treat it as active source.

## Working notes specific to this repo

- Not currently a git repository (per environment info) — don't assume `git` history/blame is available.
- `CoachingIA.Harness.csproj` pins OpenTelemetry packages with a floating `1.*` version deliberately (see README "Notes"); if you run `dotnet restore`, the README asks that resolved versions be pinned afterward rather than left floating — flag this to the user rather than silently changing it.
- `CaptureContent: false` (in `appsettings.json`, under `Harness`) must keep working as a hard content cutoff — no prompt text or tool-output text may reach OpenTelemetry export when it's off. Any change touching `SpanFactory` or hook payload handling should preserve this.
- `bilan`/`retro` write into the output directory (`bilans/` by default) via `ReviewArchive`, which intentionally refuses to overwrite a changed file silently (it archives the previous version under `bilans/archives/`, timestamped by that file's own mtime) and is a no-op when regenerated output is byte-identical. Don't "simplify" this into a plain overwrite.
