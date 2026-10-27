#:property TargetFramework=net10.0

// Design-token release helpers for .github/workflows/nuget-release.yml, as a .NET 10 file-based app.
//
//   dotnet run --file .github/scripts/design-tools.cs -- ingest    <archive-path-or-https-url> <tokens-dir>
//   dotnet run --file .github/scripts/design-tools.cs -- validate  <tokens-dir>
//   dotnet run --file .github/scripts/design-tools.cs -- generate  <tokens-dir> <generator-dir> <product> <css-dir>
//   dotnet run --file .github/scripts/design-tools.cs -- stamp-css <css-dir> <version> <package-id> <release-date>
//
// The TargetFramework directive overrides the repo-root Directory.Build.props, which sets netstandard2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

/// <summary>
/// Ingests, validates, compiles and stamps the DTCG design tokens released as the Mycelium.Design packages.
/// </summary>
internal static class DesignTools
{
    /// <summary>The shared primitives file every export must contain.</summary>
    private const string PrimitivesFile = "mycelium-primitives.json";

    /// <summary>The semantics file every product/mode folder must contain.</summary>
    private const string SemanticsFile = "mycelium-semantics.json";

    /// <summary>The folder holding the tokens shared by every product and mode.</summary>
    private const string SharedDirectory = "shared";

    /// <summary>The Tailwind v4 theme mapping file produced for each product.</summary>
    private const string ThemeFile = "theme-mycelium.css";

    /// <summary>The header the generator writes at the top of every CSS file.</summary>
    private const string GeneratedHeader = "/**\n * Do not edit directly, this file was auto-generated.";

    /// <summary>The products a package is released for.</summary>
    private static readonly string[] Products = ["forge", "bloom"];

    /// <summary>The colour modes every product defines.</summary>
    private static readonly string[] Modes = ["light", "dark"];

    /// <summary>The token types defined by the DTCG Format Module.</summary>
    private static readonly HashSet<string> TokenTypes =
    [
        "color", "dimension", "fontFamily", "fontWeight", "duration", "cubicBezier", "number",
        "strokeStyle", "border", "transition", "shadow", "gradient", "typography",
    ];

    /// <summary>The units a DTCG dimension may use.</summary>
    private static readonly HashSet<string> DimensionUnits = ["px", "rem"];

    /// <summary>The units a DTCG duration may use.</summary>
    private static readonly HashSet<string> DurationUnits = ["ms", "s"];

    /// <summary>Matches a DTCG alias such as <c>{color.gray.100}</c>, capturing the referenced path.</summary>
    private static readonly Regex Alias = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    /// <summary>Matches a CSS custom property declaration, capturing its name and value.</summary>
    private static readonly Regex Declaration = new(@"^\s*(--[^:\n]+):\s*([^;\n]*);", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Values the generator emits when it fails to render a token.</summary>
    private static readonly string[] UnrenderedValues = ["undefined", "[object Object]", "NaN"];

    /// <summary>The time allowed for a download or an external process.</summary>
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Strict UTF-8 without a byte order mark.</summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// Dispatches the command named by the first argument.
    /// </summary>
    /// <param name="args">The command followed by its arguments.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "ingest" when args.Length == 3:
                await IngestAsync(args[1], args[2]);
                return 0;

            case "validate" when args.Length == 2:
                Validate(args[1]);
                return 0;

            case "generate" when args.Length == 5:
                await GenerateAsync(args[1], args[2], RequireProduct(args[3]), args[4]);
                return 0;

            case "stamp-css" when args.Length == 5:
                StampCss(args[1], args[2], args[3], args[4]);
                return 0;

            default:
                Fail(
                    "usage:\n"
                    + "  ingest    <archive-path-or-https-url> <tokens-dir>\n"
                    + "  validate  <tokens-dir>\n"
                    + "  generate  <tokens-dir> <generator-dir> <forge|bloom> <css-dir>\n"
                    + "  stamp-css <css-dir> <version> <package-id> <release-date>");
                return 1;
        }
    }

    /// <summary>
    /// Writes the message to standard error and exits with code 1.
    /// </summary>
    /// <param name="message">The failure description.</param>
    [DoesNotReturn]
    private static void Fail(string message)
    {
        Console.Error.WriteLine("design-tools: " + message);
        Environment.Exit(1);
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// Fails unless the product is one of <see cref="Products" />.
    /// </summary>
    /// <param name="product">The product name to check.</param>
    /// <returns>The validated product name.</returns>
    private static string RequireProduct(string product)
    {
        if (!Products.Contains(product))
        {
            Fail($"unknown product '{product}'; expected {string.Join(" or ", Products)}");
        }

        return product;
    }

    /// <summary>
    /// Gets the token files, relative to the tokens folder, that every export must contain.
    /// </summary>
    /// <returns>The relative paths of the required files.</returns>
    private static IEnumerable<string> RequiredFiles() =>
        Products
            .SelectMany(product => Modes.Select(mode => Path.Combine($"{product}-{mode}", SemanticsFile)))
            .Prepend(Path.Combine(SharedDirectory, PrimitivesFile));

    // ---------------------------------------------------------------------------------------------
    // ingest
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Replaces the contents of the tokens folder with the JSON files of a designer export archive.
    /// </summary>
    /// <param name="archive">A local path or an https URL to the zip archive.</param>
    /// <param name="tokensDirectory">The folder whose contents are replaced.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private static async Task IngestAsync(string archive, string tokensDirectory)
    {
        var workspace = Directory.CreateTempSubdirectory("mycelium-design-");

        try
        {
            var archivePath = archive.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? await DownloadAsync(archive, workspace.FullName)
                : archive;

            if (!File.Exists(archivePath))
            {
                Fail($"archive {archivePath} does not exist");
            }

            var extracted = Path.Combine(workspace.FullName, "extracted");

            try
            {
                // Rejects entries that would resolve outside the destination directory.
                ZipFile.ExtractToDirectory(archivePath, extracted);
            }
            catch (Exception error) when (error is InvalidDataException or IOException)
            {
                Fail($"{archive} is not a usable zip archive: {error.Message}");
            }

            var root = FindTokensRoot(extracted, archive);

            Directory.CreateDirectory(tokensDirectory);
            foreach (var entry in new DirectoryInfo(tokensDirectory).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo directory)
                {
                    directory.Delete(true);
                }
                else
                {
                    entry.Delete();
                }
            }

            var copied = 0;
            foreach (var source in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
            {
                var target = Path.Combine(tokensDirectory, Path.GetRelativePath(root, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                copied++;
            }

            Console.WriteLine($"design-tools: {archive} -> {tokensDirectory} ({copied} token files)");
        }
        finally
        {
            workspace.Delete(true);
        }
    }

    /// <summary>
    /// Downloads the archive into the workspace.
    /// </summary>
    /// <param name="url">The https URL of the archive.</param>
    /// <param name="workspace">The scratch folder to download into.</param>
    /// <returns>The path of the downloaded file.</returns>
    private static async Task<string> DownloadAsync(string url, string workspace)
    {
        using var client = new HttpClient { Timeout = ProcessTimeout };
        var path = Path.Combine(workspace, "archive.zip");

        try
        {
            await using var stream = await client.GetStreamAsync(url);
            await using var file = File.Create(path);
            await stream.CopyToAsync(file);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            Fail($"could not download {url}: {error.Message}");
        }

        return path;
    }

    /// <summary>
    /// Finds the one extracted folder holding every required file; exports are zipped flat or in a wrapping folder.
    /// </summary>
    /// <param name="extracted">The extraction folder.</param>
    /// <param name="archive">The archive name used in error messages.</param>
    /// <returns>The folder to ingest from.</returns>
    private static string FindTokensRoot(string extracted, string archive)
    {
        var candidates = Directory.EnumerateDirectories(extracted, "*", SearchOption.AllDirectories)
            .Prepend(extracted)
            .Where(directory => RequiredFiles().All(file => File.Exists(Path.Combine(directory, file))))
            .ToList();

        if (candidates.Count != 1)
        {
            Fail(
                $"{archive} must contain exactly one folder holding {string.Join(", ", RequiredFiles())}; "
                + $"found {candidates.Count}");
        }

        return candidates[0];
    }

    // ---------------------------------------------------------------------------------------------
    // validate
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A DTCG token flattened to its dotted path.
    /// </summary>
    /// <param name="Path">The dotted token path.</param>
    /// <param name="Type">The resolved <c>$type</c>, including one inherited from a group.</param>
    /// <param name="Value">The raw <c>$value</c>.</param>
    /// <param name="File">The declaring file, relative to the tokens folder.</param>
    private sealed record Token(string Path, string? Type, JsonNode? Value, string File);

    /// <summary>
    /// Fails with every validation blocker found in the tokens folder.
    /// </summary>
    /// <param name="tokensDirectory">The tokens folder to validate.</param>
    private static void Validate(string tokensDirectory)
    {
        var errors = new List<string>();

        foreach (var file in RequiredFiles().Where(file => !File.Exists(Path.Combine(tokensDirectory, file))))
        {
            errors.Add($"missing required file {file}");
        }

        if (errors.Count > 0)
        {
            Report(tokensDirectory, errors);
        }

        var shared = LoadTokens(Path.Combine(tokensDirectory, SharedDirectory), tokensDirectory, errors);

        foreach (var product in Products)
        {
            var semanticsByMode = new Dictionary<string, List<Token>>();

            foreach (var mode in Modes)
            {
                var semantics = LoadTokens(Path.Combine(tokensDirectory, $"{product}-{mode}"), tokensDirectory, errors);
                semanticsByMode[mode] = semantics;

                foreach (var token in semantics.Where(token => token.Value is not JsonValue value
                             || value.GetValueKind() != JsonValueKind.String
                             || !Alias.IsMatch(value.GetValue<string>())))
                {
                    errors.Add($"{token.File}: semantic token {token.Path} must alias a primitive, not hold a literal");
                }

                var context = shared.GroupBy(token => token.Path).ToDictionary(group => group.Key, group => group.Last());
                foreach (var token in semantics)
                {
                    // A semantic token aliasing its own path re-exports the shared primitive it shadows.
                    if (token.Value is JsonValue value && value.ToString() == $"{{{token.Path}}}")
                    {
                        if (!context.ContainsKey(token.Path))
                        {
                            errors.Add($"{token.File}: {token.Path} re-exports a primitive that does not exist");
                        }

                        continue;
                    }

                    context[token.Path] = token;
                }

                CheckAliases(context, errors);
            }

            var light = semanticsByMode["light"].Select(token => token.Path).ToHashSet();
            var dark = semanticsByMode["dark"].Select(token => token.Path).ToHashSet();

            errors.AddRange(light.Except(dark).Select(path => $"{product}: {path} has a light value but no dark value"));
            errors.AddRange(dark.Except(light).Select(path => $"{product}: {path} has a dark value but no light value"));
        }

        Report(tokensDirectory, errors);
    }

    /// <summary>
    /// Fails if any error was collected, otherwise reports the folder as valid.
    /// </summary>
    /// <param name="tokensDirectory">The validated tokens folder.</param>
    /// <param name="errors">The collected validation blockers.</param>
    private static void Report(string tokensDirectory, List<string> errors)
    {
        if (errors.Count > 0)
        {
            Fail($"{tokensDirectory} has {errors.Count} validation blocker(s):\n  " + string.Join("\n  ", errors.Distinct()));
        }

        Console.WriteLine($"design-tools: {tokensDirectory} is valid");
    }

    /// <summary>
    /// Loads and flattens every JSON file under a folder.
    /// </summary>
    /// <param name="directory">The folder to load.</param>
    /// <param name="tokensDirectory">The tokens folder that file names are reported relative to.</param>
    /// <param name="errors">Receives parse and structure errors.</param>
    /// <returns>The tokens found, in file order.</returns>
    private static List<Token> LoadTokens(string directory, string tokensDirectory, List<string> errors)
    {
        var tokens = new List<Token>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var file = Path.GetRelativePath(tokensDirectory, path).Replace('\\', '/');

            JsonNode? document;
            try
            {
                document = JsonNode.Parse(Utf8.GetString(File.ReadAllBytes(path)));
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException)
            {
                errors.Add($"{file}: not valid UTF-8 JSON: {error.Message}");
                continue;
            }

            if (document is not JsonObject root)
            {
                errors.Add($"{file}: top level must be a JSON object");
                continue;
            }

            CollectTokens(root, [], null, file, tokens, errors);
        }

        return tokens;
    }

    /// <summary>
    /// Recursively flattens a DTCG group into tokens, checking names, types and units on the way.
    /// </summary>
    /// <param name="group">The group or token object.</param>
    /// <param name="path">The path segments leading to the group.</param>
    /// <param name="inheritedType">The <c>$type</c> inherited from the enclosing groups.</param>
    /// <param name="file">The declaring file, relative to the tokens folder.</param>
    /// <param name="tokens">Receives the tokens found.</param>
    /// <param name="errors">Receives the violations found.</param>
    private static void CollectTokens(
        JsonObject group, List<string> path, string? inheritedType, string file, List<Token> tokens, List<string> errors)
    {
        var type = group["$type"]?.GetValue<string>() ?? inheritedType;

        if (group.ContainsKey("$value"))
        {
            var name = string.Join('.', path);

            if (type is null || !TokenTypes.Contains(type))
            {
                errors.Add($"{file}: {name} has unknown or missing $type '{type}'");
            }
            else
            {
                CheckUnits(group["$value"], type, $"{file}: {name}", errors);
            }

            tokens.Add(new Token(name, type, group["$value"], file));
            return;
        }

        foreach (var (key, child) in group)
        {
            if (key.StartsWith('$'))
            {
                continue;
            }

            // DTCG Format Module, "Character restrictions".
            if (key.IndexOfAny(['{', '}', '.']) >= 0)
            {
                errors.Add($"{file}: name '{key}' under '{string.Join('.', path)}' contains '{{', '}}' or '.'");
            }

            if (child is JsonObject childGroup)
            {
                CollectTokens(childGroup, [.. path, key], type, file, tokens, errors);
            }
        }
    }

    /// <summary>
    /// Checks every dimension-like object inside a token value against the units allowed for its type.
    /// </summary>
    /// <param name="value">The token value, or a nested part of it.</param>
    /// <param name="type">The token's <c>$type</c>.</param>
    /// <param name="location">The token location used in error messages.</param>
    /// <param name="errors">Receives the violations found.</param>
    private static void CheckUnits(JsonNode? value, string type, string location, List<string> errors)
    {
        switch (value)
        {
            case JsonObject dimension when dimension["unit"] is JsonValue unitNode:
                var unit = unitNode.ToString();
                var allowed = type switch
                {
                    "duration" => DurationUnits,
                    "dimension" => DimensionUnits,
                    _ => [.. DimensionUnits, .. DurationUnits],
                };

                if (!allowed.Contains(unit))
                {
                    errors.Add($"{location} uses unit '{unit}'; allowed: {string.Join(", ", allowed)}");
                }

                break;

            case JsonObject composite:
                foreach (var (_, child) in composite)
                {
                    CheckUnits(child, type, location, errors);
                }

                break;

            case JsonArray array:
                foreach (var child in array)
                {
                    CheckUnits(child, type, location, errors);
                }

                break;
        }
    }

    /// <summary>
    /// Reports aliases that resolve to no token and aliases that form a cycle.
    /// </summary>
    /// <param name="context">The tokens visible to one product and mode, by path.</param>
    /// <param name="errors">Receives the violations found.</param>
    private static void CheckAliases(Dictionary<string, Token> context, List<string> errors)
    {
        var references = context.ToDictionary(
            pair => pair.Key,
            pair => Strings(pair.Value.Value)
                .SelectMany(text => Alias.Matches(text).Select(match => match.Groups[1].Value))
                .ToList());

        foreach (var (path, targets) in references)
        {
            foreach (var target in targets.Where(target => !context.ContainsKey(target)))
            {
                errors.Add($"{context[path].File}: {path} references undefined token {{{target}}}");
            }
        }

        var state = new Dictionary<string, bool>();

        bool HasCycle(string path)
        {
            if (state.TryGetValue(path, out var done))
            {
                return !done;
            }

            state[path] = false;
            var cyclic = references[path].Where(references.ContainsKey).Any(HasCycle);
            state[path] = true;
            return cyclic;
        }

        foreach (var path in references.Keys.Where(path => !state.ContainsKey(path) && HasCycle(path)))
        {
            errors.Add($"{context[path].File}: {path} is part of an alias cycle");
        }
    }

    /// <summary>
    /// Enumerates every string inside a JSON value.
    /// </summary>
    /// <param name="node">The JSON value to search.</param>
    /// <returns>The strings found, depth first.</returns>
    private static IEnumerable<string> Strings(JsonNode? node) =>
        node switch
        {
            JsonObject composite => composite.SelectMany(pair => Strings(pair.Value)),
            JsonArray array => array.SelectMany(Strings),
            JsonValue value when value.GetValueKind() == JsonValueKind.String => [value.GetValue<string>()],
            _ => [],
        };

    // ---------------------------------------------------------------------------------------------
    // generate
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Generates and verifies the CSS of one product, then replaces the CSS files in the target folder.
    /// </summary>
    /// <param name="tokensDirectory">The validated tokens folder.</param>
    /// <param name="generatorDirectory">The folder holding the Node.js generator.</param>
    /// <param name="product">The product to generate.</param>
    /// <param name="cssDirectory">The folder receiving the CSS files.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private static async Task GenerateAsync(string tokensDirectory, string generatorDirectory, string product, string cssDirectory)
    {
        var workspace = Directory.CreateTempSubdirectory("mycelium-design-");

        try
        {
            string[] npmCi = ["npm", "ci", "--ignore-scripts", "--no-audit", "--no-fund"];
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await RunAsync("cmd.exe", ["/c", .. npmCi], generatorDirectory);
            }
            else
            {
                await RunAsync(npmCi[0], npmCi[1..], generatorDirectory);
            }

            var expected = new[] { $"tokens-{product}.css", ThemeFile };
            var runs = new List<string>();

            // Generated twice so a nondeterministic transform fails here rather than as a spurious diff.
            for (var run = 1; run <= 2; run++)
            {
                var output = Path.Combine(workspace.FullName, $"run{run}");
                await RunAsync(
                    "node",
                    [
                        "build-tokens.mjs",
                        $"--target={product}",
                        $"--tokensDir={Path.GetFullPath(tokensDirectory)}",
                        $"--outDir={output}",
                    ],
                    generatorDirectory);

                VerifyOutput(output, product, expected);
                runs.Add(output);
            }

            foreach (var file in expected.Where(file => !File.ReadAllBytes(Path.Combine(runs[0], file))
                         .SequenceEqual(File.ReadAllBytes(Path.Combine(runs[1], file)))))
            {
                Fail($"{file} differs between two generations from the same tokens; the transform is not deterministic");
            }

            Directory.CreateDirectory(cssDirectory);
            foreach (var stale in Directory.EnumerateFiles(cssDirectory, "*.css"))
            {
                File.Delete(stale);
            }

            foreach (var file in expected)
            {
                File.Copy(Path.Combine(runs[0], file), Path.Combine(cssDirectory, file));
            }

            Console.WriteLine($"design-tools: {product} -> {cssDirectory} ({string.Join(", ", expected)})");
        }
        finally
        {
            workspace.Delete(true);
        }
    }

    /// <summary>
    /// Fails unless a generation produced exactly the expected, fully rendered CSS files.
    /// </summary>
    /// <param name="directory">The generation output folder.</param>
    /// <param name="product">The generated product.</param>
    /// <param name="expected">The file names the generation must produce.</param>
    private static void VerifyOutput(string directory, string product, string[] expected)
    {
        var errors = new List<string>();

        var unexpected = Directory.EnumerateFiles(directory).Select(Path.GetFileName).Except(expected).ToList();
        if (unexpected.Count > 0)
        {
            errors.Add($"unexpected output {string.Join(", ", unexpected)}");
        }

        foreach (var file in expected)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path))
            {
                errors.Add($"{file} was not generated");
                continue;
            }

            var content = File.ReadAllText(path, Utf8);

            if (!content.StartsWith(GeneratedHeader, StringComparison.Ordinal))
            {
                errors.Add($"{file} does not start with the generated-file header");
            }

            foreach (Match declaration in Declaration.Matches(content))
            {
                var value = declaration.Groups[2].Value;
                if (value.Length == 0 || Alias.IsMatch(value) || UnrenderedValues.Any(value.Contains))
                {
                    errors.Add($"{file}: {declaration.Groups[1].Value} has unusable value '{value}'");
                }
            }
        }

        var tokens = Path.Combine(directory, $"tokens-{product}.css");
        var theme = Path.Combine(directory, ThemeFile);

        if (File.Exists(tokens))
        {
            var content = File.ReadAllText(tokens, Utf8);
            errors.AddRange(new[] { ":root {", ".dark {", "--background:" }
                .Where(marker => !content.Contains(marker, StringComparison.Ordinal))
                .Select(marker => $"tokens-{product}.css lacks '{marker}'"));
        }

        if (File.Exists(theme))
        {
            var content = File.ReadAllText(theme, Utf8);
            errors.AddRange(new[] { "@theme inline {", "--color-", "--radius-", "--spacing-", "--text-" }
                .Where(marker => !content.Contains(marker, StringComparison.Ordinal))
                .Select(marker => $"{ThemeFile} lacks '{marker}'"));
        }

        if (errors.Count > 0)
        {
            Fail($"generated CSS for {product} failed verification:\n  " + string.Join("\n  ", errors));
        }
    }

    /// <summary>
    /// Runs an external process, echoing its output and failing on a non-zero exit code or a timeout.
    /// </summary>
    /// <param name="fileName">The executable to start.</param>
    /// <param name="arguments">The arguments, passed without shell quoting.</param>
    /// <param name="workingDirectory">The working directory of the process.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private static async Task RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeout = new System.Threading.CancellationTokenSource(ProcessTimeout);
        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            Fail($"could not start {fileName}: {error.Message}");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            Fail($"{fileName} timed out after {ProcessTimeout.TotalMinutes} minutes");
        }

        Console.Write(await standardOutput);
        var errorOutput = await standardError;

        if (process.ExitCode != 0)
        {
            Fail($"{fileName} {string.Join(' ', arguments)} exited with {process.ExitCode}:\n{errorOutput}");
        }

        Console.Error.Write(errorOutput);
    }

    // ---------------------------------------------------------------------------------------------
    // stamp-css
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Adds or updates the release metadata lines in the header of every CSS file in a folder.
    /// </summary>
    /// <param name="cssDirectory">The folder holding the CSS files.</param>
    /// <param name="version">The released package version.</param>
    /// <param name="packageId">The released package id.</param>
    /// <param name="releaseDate">The release date, as yyyy-MM-dd.</param>
    private static void StampCss(string cssDirectory, string version, string packageId, string releaseDate)
    {
        var files = Directory.EnumerateFiles(cssDirectory, "*.css").Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            Fail($"no .css files found in {cssDirectory}");
        }

        foreach (var path in files)
        {
            var content = File.ReadAllText(path, Utf8);
            var headerEnd = content.IndexOf(" */\n", StringComparison.Ordinal);

            if (!content.StartsWith("/**\n", StringComparison.Ordinal) || headerEnd == -1)
            {
                Fail($"{path} has no leading /** ... */ header to stamp");
            }

            var header = content[..headerEnd];

            foreach (var (key, value) in new[]
                     {
                         ("mycelium-version", version),
                         ("mycelium-packageId", packageId),
                         ("mycelium-releaseDate", releaseDate),
                     })
            {
                var line = $" * {key}: {value}\n";
                var existing = Regex.Match(header, $@"^ \* {Regex.Escape(key)}: .*\n", RegexOptions.Multiline);
                header = existing.Success
                    ? header[..existing.Index] + line + header[(existing.Index + existing.Length)..]
                    : header + line;
            }

            File.WriteAllText(path, header + content[headerEnd..], Utf8);
        }

        Console.WriteLine(
            $"design-tools: {cssDirectory} <- version={version} packageId={packageId} releaseDate={releaseDate} "
            + $"({files.Count} files)");
    }
}
