# Agent instructions

## Project overview

- Intro Skipper is a Jellyfin plugin targeting Jellyfin `10.11` and .NET `9.0`.
- Main plugin code lives in `IntroSkipper/`; xUnit tests live in `IntroSkipper.Tests/`.
- The dashboard/web assets live in `web/` and are built with Vite into `IntroSkipper/Configuration/` as embedded resources.
- Keep changes compatible with the current `10.11` branch unless explicitly asked otherwise.

## Build and test commands

- Restore dependencies: `dotnet restore`
- Build plugin and web assets: `dotnet build --no-restore`
- Run tests: `dotnet test --no-restore --verbosity normal`
- Build only the web assets: `cd web && pnpm install --frozen-lockfile && pnpm build`
- The C# build invokes the web build automatically unless `SkipWebBuild=true` is passed.
- Media-related tests expect Jellyfin FFmpeg with Chromaprint support. In CI this is installed from the `jellyfin-ffmpeg7` package and symlinked as `ffmpeg`/`ffprobe`.

## Coding conventions

- Follow `.editorconfig`; warnings are treated as errors in `IntroSkipper/IntroSkipper.csproj`.
- C# files use nullable reference types, implicit usings, StyleCop analyzers, and XML documentation where required.
- New C# files must include SPDX copyright/license headers consistent with nearby files.
- Prefer existing project patterns for dependency injection, scheduled tasks, EF Core migrations, managers, providers, and services.
- Keep public API and Jellyfin integration changes conservative; Jellyfin package versions are pinned to `10.11.*-*`.

## Web conventions

- Use TypeScript and existing Vite patterns under `web/src/`.
- Do not hand-edit generated dashboard output in `IntroSkipper/Configuration/` when the source lives in `web/src/`; change the source and rebuild instead.
- Preserve the existing embedded resource names: `configPage.html`, `introskipper.js`, and `introskipper.css`.

## Review checklist

- Verify C# analyzer/style requirements before finishing non-doc changes.
- For changes touching detection, FFmpeg, database/cache, or chapter analysis behavior, add or update tests in `IntroSkipper.Tests/`.
- For web changes, ensure `pnpm build` succeeds and the generated embedded resources are updated when needed.
