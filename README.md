# mycelium-design

The design-token CSS for Mycelium. The designer's DTCG export of the shared Figma library is validated, compiled into CSS and released as one independently-versioned NuGet package per product:

| Product | Project | Package | Contents |
|---|---|---|---|
| Forge | `Mycelium.Design.Forge/` | `Mycelium.Design.Forge` | `css/tokens-forge.css`, `css/theme-mycelium.css` |
| Bloom | `Mycelium.Design.Bloom/` | `Mycelium.Design.Bloom` | `css/tokens-bloom.css`, `css/theme-mycelium.css` |

- `tokens-<product>.css` holds the runtime custom properties: light values on `:root`, dark values on `.dark`. Swappable at runtime with no rebuild, and directly consumable from scoped `.razor.css`.
- `theme-mycelium.css` holds the Tailwind v4 `@theme inline` mapping onto those variables. It is build-time only and must be imported before Tailwind compiles.

Both files carry a generated-file header. Never edit them by hand.

## Repository layout

| Path | Role |
|---|---|
| `generator/` | The Style Dictionary transform (Node.js) from DTCG to CSS. Node is needed here and nowhere else. |
| `.github/scripts/design-tools.cs` | Ingestion, validation, generation and stamping, as a .NET 10 file-based app. |
| `Mycelium.Design.<Product>/` | Content-only packaging project; `css/` holds the CSS of the last release. |

## Consuming a package

Adding a `PackageReference` imports MSBuild properties pointing at the packed CSS inside the local NuGet cache:

| Package | MSBuild properties |
|---|---|
| `Mycelium.Design.Forge` | `$(MyceliumDesignForgeTokensCssPath)`, `$(MyceliumDesignForgeThemeCssPath)`, `$(MyceliumDesignForgeCssDir)` |
| `Mycelium.Design.Bloom` | `$(MyceliumDesignBloomTokensCssPath)`, `$(MyceliumDesignBloomThemeCssPath)`, `$(MyceliumDesignBloomCssDir)` |

The standalone Tailwind CLI resolves `@import` relative to its entry file and cannot reach into `~/.nuget/packages/`, so copy the theme next to the entry file before Tailwind runs:

```xml
<PackageReference Include="Mycelium.Design.Forge" Version="1.0.0" />

<Target Name="CopyMyceliumTheme" BeforeTargets="BuildTailwind">
  <Copy SourceFiles="$(MyceliumDesignForgeThemeCssPath)" DestinationFolder="$(IntermediateOutputPath)mycelium/" SkipUnchangedFiles="true" />
  <Copy SourceFiles="$(MyceliumDesignForgeTokensCssPath)" DestinationFolder="wwwroot/css/" SkipUnchangedFiles="true" />
</Target>
```

To preview CSS generated locally before it is released, preset the directory property:

```
dotnet build -p:MyceliumDesignForgeCssDir=../../mycelium-design/Mycelium.Design.Forge/css/
```

## Working locally

Prerequisites: the .NET 10 SDK and Node.js 24 with npm.

The extracted export is working data and is never committed; `tokens/` is git-ignored for local runs.

```
dotnet run --file .github/scripts/design-tools.cs -- ingest    <designer-export.zip> tokens
dotnet run --file .github/scripts/design-tools.cs -- validate  tokens
dotnet run --file .github/scripts/design-tools.cs -- generate  tokens generator forge Mycelium.Design.Forge/css
dotnet run --file .github/scripts/design-tools.cs -- stamp-css Mycelium.Design.Forge/css <version> <package-id> <release-date>
dotnet pack -c Release -o ReleaseBuilds Mycelium.Design.Forge/Mycelium.Design.Forge.csproj
```

Use the explicit `--file` form: the repository root contains `mycelium-design.sln`, and `dotnet run`'s first-argument form only applies when there is no project in the current directory.

### ingest

Extracts the archive (an https URL or a local path) and replaces the contents of the target folder with its `.json` files: `shared/` primitives, typography and shadows, plus `<product>-<mode>/mycelium-semantics.json` for `forge`/`bloom` × `light`/`dark`. The export may be zipped flat or inside one wrapping folder; the folder holding `shared/mycelium-primitives.json` and the four `<product>-<mode>/mycelium-semantics.json` files becomes the root. Anything else in the archive is ignored.

### validate

Every rule is a release blocker:

- the five required files exist and every `.json` file is UTF-8 JSON with an object at the top level;
- every token resolves a known DTCG `$type`, directly or inherited from its group;
- no token or group name contains `{`, `}` or `.`;
- dimensions use `px` or `rem`, durations `ms` or `s`;
- every semantic token is an alias, never a literal value;
- every alias resolves against the shared files plus that product/mode's semantics, and no alias chain is cyclic;
- light and dark semantics of each product define the same token set, so `.dark` never silently falls back to a light value.

A semantic token aliasing its own path (`"spacing.1": "{spacing.1}"`) re-exports the shared primitive it shadows, as the generator's normaliser treats it.

### generate

Runs `npm ci` in `generator/`, then generates the selected product twice into scratch directories. It fails unless both runs are byte-identical, both files carry the generated-file header, the expected `:root`, `.dark` and `@theme inline` blocks and namespaces are present, and no declaration is empty, unresolved (`{…}`) or unrendered (`undefined`, `[object Object]`, `NaN`). It then replaces the `.css` files in the target directory.

Each product is generated with its own `--target`, so its `theme-mycelium.css` comes from its own light semantics.

### stamp-css

Adds or updates `mycelium-version`, `mycelium-packageId` and `mycelium-releaseDate` lines in each file's generated header.

## Releasing

Releases are cut manually through the `Nuget-Release` workflow (`workflow_dispatch`):

- **product**: `forge` or `bloom`.
- **version**: SemVer for that package's next release.
- **archive**: the designer export, as an https URL or a repository-relative path.

A run stamps the version into `CITATION.cff` and the project's `<Version>`, ingests the archive into the runner's temporary folder, validates the tokens, generates and stamps the CSS, packs (without `-p:Version`; the `.csproj` element is the single source of the version), commits the CSS, the `.csproj` and `CITATION.cff` back to the default branch, pushes the package to NuGet.org, tags `<product>/<version>` and drafts a GitHub release with the `.nupkg` attached.

`CITATION.cff` always reflects whichever package released most recently.

The commit step pushes directly to the default branch, so two runs started at the same moment can conflict; re-run the second one.

## Adding another product

1. Copy an existing `Mycelium.Design.<Product>/` folder and rename the project, props files and MSBuild properties.
2. Add `<product>-light/` and `<product>-dark/` to the designer export and `<product>` to `Products` in `.github/scripts/design-tools.cs` and to `buildTokens` in `generator/token-builder.mjs`.
3. `dotnet sln mycelium-design.sln add Mycelium.Design.<Product>/Mycelium.Design.<Product>.csproj`.
4. Add the product to the `product` choice and the `Resolve project` step in `.github/workflows/nuget-release.yml`.
