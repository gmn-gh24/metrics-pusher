# Repository Guidelines

## Project Structure & Module Organization

MetricsPusher is a Windows-only .NET 10 WinForms tray application. Entry-point and UI code live in `Program.cs` and `TrayApplicationContext.cs`; hardware, networking, logging, and payload logic live under `Services/`. Embedded icons, the PawnIO installer, signed modules, and license text are in `Resources/`. Protocol behavior is documented in `push_metrics.md`, while user-facing setup and security guidance belongs in `README.md`. Tests are in the sibling `MetricsPusher.Tests/` xUnit project and generally mirror production class names.

## Build, Test, and Development Commands

Run commands from the repository root with the .NET 10 SDK:

```powershell
dotnet restore --locked-mode
dotnet build --warnaserror
dotnet test
dotnet test --filter "FullyQualifiedName~GpuDisplayPushServiceTests"
dotnet publish MetricsPusher.csproj -c Release -r win-x64 --self-contained -o "publish"
```

Locked restore verifies `packages.lock.json`. Build must pass StyleCop, Roslynator, and .NET analyzers without warnings. The publish command creates the distributable single-file `publish/MetricsPusher.exe`; do not commit `bin/`, `obj/`, or `publish/` output.

## Coding Style & Naming Conventions

Follow `.editorconfig`: four spaces for C#, two for XML/JSON, CRLF endings, braces on new lines, file-scoped namespaces, nullable reference types, and System usings first. Use PascalCase for types and members, `_camelCase` for private fields, and descriptive names over abbreviations. Treat analyzer findings as build failures. Preserve existing service boundaries and avoid unrelated cleanup in hardware-sensitive code.

## Testing Guidelines

Use xUnit `[Fact]` and `[Theory]` tests. Name test files `<TypeName>Tests.cs` and methods `Member_ShouldExpectedBehavior_WhenCondition`. Add regression tests for behavior changes and edge cases. Wire-format changes must update `push_metrics.md`, payload assertions, and `MaxDatagramBytes` expectations together. No numeric coverage threshold is defined, but new logic should be exercised before submission.

## Commit & Pull Request Guidelines

History uses short, imperative, sentence-case subjects such as `Harden runtime security and network handling`. Keep commits focused and explain security or protocol tradeoffs in the body. Pull requests should summarize behavior, list validation commands, link relevant issues, and call out wire-schema, elevation, PawnIO, native-library, or dependency-lock changes. Include screenshots only for visible tray/UI changes.

Use Git for Windows and standard `git` commands. Do not require or invoke GitHub CLI (`gh`). For explicit commit-and-push requests, inspect the diff, stage only requested files, commit, and push the current branch to its configured remote.

## Security & Configuration Tips

Never enable PawnIO's unrestricted mode or replace bundled binaries without verifying provenance, hashes, licensing, and updating `README.md`. Pin new P/Invoke libraries through `SystemLibraryResolver`; do not weaken private-network destination checks. Never commit secrets or local `.env` files.
