# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

The single source of the Mycelium design-token CSS. The designer's DTCG export of the shared Figma library (a zip, never committed) is validated and compiled into CSS, which is released as two independently-versioned, content-only NuGet packages: `Mycelium.Design.Forge` and `Mycelium.Design.Bloom`. The projects are `netstandard2.0` with `IncludeBuildOutput=false`; the CSS is packed under `css/`, and `build/` + `buildTransitive/` props expose its absolute paths as MSBuild properties.

`README.md` documents the layout, every validation rule and the release flow; read it before changing behaviour.

## Commands

```
dotnet build mycelium-design.sln
dotnet run --file .github/scripts/design-tools.cs -- ingest <designer-export.zip> tokens
dotnet run --file .github/scripts/design-tools.cs -- validate tokens
dotnet run --file .github/scripts/design-tools.cs -- generate tokens generator forge Mycelium.Design.Forge/css
dotnet pack -c Release -o ReleaseBuilds Mycelium.Design.Forge/Mycelium.Design.Forge.csproj
```

`design-tools.cs` is a .NET 10 file-based app (no `.csproj`); its `#:property TargetFramework=net10.0` overrides the repo-root `Directory.Build.props`. Use the explicit `--file` form because the root holds `mycelium-design.sln`. `generate` needs Node.js 24 and npm; nothing else does.

Always pack a specific `.csproj`, never the solution, and never pass `-p:Version` to a release pack: the `<Version>` element stamped by the workflow is the single source of the published version.

## Things that are easy to get wrong

- The generator's `--target=all` builds both products in parallel into one `theme-mycelium.css`, so the last writer wins; `design-tools.cs generate` therefore always runs one product per output directory.
- The generator dereferences the semantic `radius`, `spacing` and `text` groups against the shared primitives they shadow (`"spacing.1": "{spacing.1}"`). The validator models this; a naive alias check reports every one of them as a cycle.
- `generate` replaces every `.css` in the target directory, and `stamp-css` writes into the header of each one.

## Contribution workflow

Branch from `development` (never work on or PR directly from `main`/`development`); rebase onto `development` before sending a PR so history stays linear. Full details in `.github/CONTRIBUTING.md`.
