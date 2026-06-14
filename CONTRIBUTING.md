# Contributing to SyncSession

Thanks for your interest in contributing to SyncSession — an offline-first,
session-based data synchronization library for .NET. This guide covers how to
build, test, and submit changes.

## Ways to Contribute

- **Report bugs** — open an issue with a minimal reproduction, expected vs.
  actual behavior, and your environment (OS, .NET SDK, database).
- **Suggest features** — open an issue describing the use case before sending a
  large PR, so design can be discussed first.
- **Submit fixes / improvements** — see the pull request process below.

## Prerequisites

- **.NET SDK 8.0 and 10.0** — the solution multi-targets `net8.0` and `net10.0`.
  Install both so all targets build and test.
- **Docker** — required for the integration tests. They spin up a real MariaDB
  instance via [Testcontainers](https://testcontainers.com/); the Docker daemon
  must be running.
- A MySQL/MariaDB client is optional but useful for inspecting test databases.

## Getting Started

```bash
git clone https://github.com/gismofx/SyncSession.git
cd SyncSession
dotnet restore SyncSession.sln
dotnet build SyncSession.sln -c Release --no-restore
```

## Running Tests

The CI pipeline (`.github/workflows/test.yml`) runs these exact steps; run them
locally before opening a PR.

**Unit tests** (no Docker required):

```bash
dotnet test tests/SyncSession.UnitTests/SyncSession.UnitTests.csproj \
  -c Release --no-build --logger "console;verbosity=minimal"
```

**Integration tests** (Docker daemon must be running):

```bash
dotnet test tests/SyncSession.IntegrationTests/SyncSession.IntegrationTests.csproj \
  -c Release --no-build --logger "console;verbosity=minimal"
```

If integration tests fail to start, confirm Docker is running and can pull the
MariaDB image.

## Project Layout

| Path | Contents |
|------|----------|
| `src/` | Shipping library projects (`Core`, `Client`, `Server`) packed to NuGet |
| `tests/` | `UnitTests` and `IntegrationTests` (xUnit + FluentAssertions + Testcontainers) |
| `samples/` | Console and Avalonia desktop sample apps |
| `.github/workflows/` | CI (`test.yml`) and publish (`publish.yml`) pipelines |

## Coding Guidelines

- **Formatting** follows the repo `.editorconfig`. Run `dotnet format` before
  committing.
- **Nullable reference types** are enabled solution-wide; keep new code
  null-clean.
- **Public API XML docs are required.** Library projects in `src/` treat missing
  docs (CS1591) as build warnings; every public type and member needs a
  `<summary>` (plus `<param>`/`<returns>` where applicable). Test and sample
  projects are exempt.
- **No raw SQL in service classes** — database access goes through the
  `IServerDatabase` / `IClientDatabase` abstractions.
- Keep changes focused; one logical change per PR.

## Pull Request Process

1. Fork the repo and create a branch from `main`.
2. Make your change with accompanying tests where behavior changes.
3. Ensure `dotnet build` and both test suites pass locally.
4. Push and open a PR against `main` with a clear description of the change and
   the motivation. Reference any related issue.
5. CI must be green before review. A maintainer will review and merge.

## License

By contributing, you agree that your contributions are licensed under the
[MIT License](LICENSE), the same license that covers this project.
