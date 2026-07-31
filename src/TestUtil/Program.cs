using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Spectre.Console;
using Spectre.Console.Cli;
using TextCopy;
using ValidationResult = Spectre.Console.ValidationResult;

// Playlist file format docs: https://learn.microsoft.com/en-us/visualstudio/test/run-unit-tests-with-test-explorer?view=vs-2022#create-custom-playlists

var app = new CommandApp<PlaylistCommand>();
app.Configure(static config => config.SetApplicationName("testutil"));
return await app.RunAsync(args);

sealed class PlaylistSettings : CommandSettings
{
    [Description("Roslyn GitHub PR number or Azure DevOps build ID.")]
    [CommandArgument(0, "[NUMBER]")]
    public int? Number { get; init; }

    [Description("Output location: 'temp', 'current', or a directory path.")]
    [CommandOption("-o|--output <DIRECTORY>")]
    public string? OutputDirectory { get; init; }

    [Description("Create a playlist from find-all-references output.")]
    [CommandOption("-f|--find-all-references")]
    public bool FindAllReferences { get; init; }

    [Description("Read find-all-references output from the clipboard.")]
    [CommandOption("--clipboard")]
    public bool Clipboard { get; init; }

    [Description("Paste find-all-references output into the terminal.")]
    [CommandOption("--paste")]
    public bool Paste { get; init; }

    [Description("Convert a generated .playlist file to xUnit method filters.")]
    [CommandOption("-x|--xunit-filter <PLAYLIST>")]
    public string? XUnitFilterPlaylist { get; init; }

    [Description("Convert a generated .playlist file to a VSTest filter.")]
    [CommandOption("-v|--vstest-filter <PLAYLIST>")]
    public string? VSTestFilterPlaylist { get; init; }

    public override ValidationResult Validate()
    {
        if (Number <= 0)
        {
            return ValidationResult.Error("NUMBER must be a positive integer.");
        }

        if (Clipboard && Paste)
        {
            return ValidationResult.Error("Use either --clipboard or --paste, not both.");
        }

        if (Number is not null && (FindAllReferences || Clipboard || Paste))
        {
            return ValidationResult.Error("NUMBER cannot be combined with find-all-references options.");
        }

        if (string.IsNullOrWhiteSpace(XUnitFilterPlaylist) && XUnitFilterPlaylist is not null)
        {
            return ValidationResult.Error("--xunit-filter cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(VSTestFilterPlaylist) && VSTestFilterPlaylist is not null)
        {
            return ValidationResult.Error("--vstest-filter cannot be empty.");
        }

        if (XUnitFilterPlaylist is not null && VSTestFilterPlaylist is not null)
        {
            return ValidationResult.Error("Use either --xunit-filter or --vstest-filter, not both.");
        }

        if ((XUnitFilterPlaylist is not null || VSTestFilterPlaylist is not null) &&
            (Number is not null || FindAllReferences || Clipboard || Paste || OutputDirectory is not null))
        {
            return ValidationResult.Error("Filter conversion cannot be combined with other options or NUMBER.");
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory) && OutputDirectory is not null)
        {
            return ValidationResult.Error("--output cannot be empty.");
        }

        return ValidationResult.Success();
    }
}

sealed class PlaylistCommand : AsyncCommand<PlaylistSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        PlaylistSettings settings,
        CancellationToken cancellationToken)
    {
        var inputKind = GetInputKind(settings);
        if (inputKind is InputKind.XUnitFilter or InputKind.VSTestFilter)
        {
            var playlistFileName = settings.XUnitFilterPlaylist ?? settings.VSTestFilterPlaylist ??
                AnsiConsole.Ask<string>("Playlist file path:");
            return ConvertPlaylistFilter(playlistFileName, inputKind);
        }

        string outputDirectory;
        try
        {
            outputDirectory = GetOutputDirectory(settings.OutputDirectory);
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AnsiConsole.MarkupLine($"[red]Could not use the output directory:[/] {Markup.Escape(ex.Message)}");
            return -1;
        }

        AnsiConsole.WriteLine($"Output directory: {outputDirectory}");

        if (inputKind == InputKind.FindAllReferences)
        {
            var source = GetFindAllReferencesSource(settings);
            return await ProcessFindAllReferencesAsync(outputDirectory, source);
        }

        var number = settings.Number ?? AnsiConsole.Prompt(
            new TextPrompt<int>("PR number or build ID:")
                .ValidationErrorMessage("[red]Enter a positive integer.[/]")
                .Validate(static value => value > 0));

        return await ProcessBuildAsync(outputDirectory, number, cancellationToken);
    }

    private static int ConvertPlaylistFilter(string playlistFileName, InputKind inputKind)
    {
        try
        {
            var playlist = XDocument.Load(playlistFileName);
            var root = playlist.Root;
            var rules = root?.Elements("Rule").ToArray();
            if (root?.Name != "Playlist" ||
                (string?)root.Attribute("Version") != "2.0" ||
                rules is not [{ } rule] ||
                (string?)rule.Attribute("Match") != "Any")
            {
                AnsiConsole.MarkupLine("[red]The file is not a supported playlist.[/]");
                return -1;
            }

            var testNames = rule.Elements("Property")
                .Where(static property =>
                    (string?)property.Attribute("Name") == "TestWithNormalizedFullyQualifiedName")
                .Select(static property => (string?)property.Attribute("Value"))
                .Where(static value => !string.IsNullOrEmpty(value))
                .ToArray();

            if (testNames.Length == 0)
            {
                AnsiConsole.MarkupLine("[red]The playlist does not contain any supported tests.[/]");
                return -1;
            }

            var filter = inputKind switch
            {
                InputKind.XUnitFilter => string.Join(
                    ' ',
                    testNames.Select(static testName => $"-method \"{testName}\"")),
                InputKind.VSTestFilter => $"--filter \"{string.Join(
                    '|',
                    testNames.Select(static testName => $"FullyQualifiedName={testName}"))}\"",
                _ => throw new ArgumentOutOfRangeException(nameof(inputKind)),
            };

            Console.WriteLine(filter);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            AnsiConsole.MarkupLine($"[red]Could not read the playlist:[/] {Markup.Escape(ex.Message)}");
            return -1;
        }
    }

    private static string GetOutputDirectory(string? option)
    {
        var tempPath = Path.Join(Path.GetTempPath(), "testutil");

        if (option is null)
        {
            var location = AnsiConsole.Prompt(
                new SelectionPrompt<OutputLocation>()
                    .Title("Where should the playlist be written?")
                    .UseConverter(value => value switch
                    {
                        OutputLocation.Temp => $"Temporary directory ({tempPath})",
                        OutputLocation.Current => $"Current working directory ({Environment.CurrentDirectory})",
                        OutputLocation.Custom => "Custom directory",
                        _ => throw new ArgumentOutOfRangeException(nameof(value)),
                    })
                    .AddChoices(OutputLocation.Temp, OutputLocation.Current, OutputLocation.Custom));

            option = location switch
            {
                OutputLocation.Temp => "temp",
                OutputLocation.Current => "current",
                OutputLocation.Custom => AnsiConsole.Ask<string>("Output directory path:"),
                _ => throw new ArgumentOutOfRangeException(nameof(location)),
            };
        }

        return option.ToLowerInvariant() switch
        {
            "temp" => tempPath,
            "current" => Environment.CurrentDirectory,
            _ => Path.GetFullPath(option),
        };
    }

    private static InputKind GetInputKind(PlaylistSettings settings)
    {
        if (settings.XUnitFilterPlaylist is not null)
        {
            return InputKind.XUnitFilter;
        }

        if (settings.VSTestFilterPlaylist is not null)
        {
            return InputKind.VSTestFilter;
        }

        if (settings.Number is not null)
        {
            return InputKind.Number;
        }

        if (settings.FindAllReferences || settings.Clipboard || settings.Paste)
        {
            return InputKind.FindAllReferences;
        }

        return AnsiConsole.Prompt(
            new SelectionPrompt<InputKind>()
                .Title("What would you like to do?")
                .UseConverter(static value => value switch
                {
                    InputKind.Number => "Create a playlist from a PR number or build ID",
                    InputKind.FindAllReferences => "Create a playlist from find-all-references output",
                    InputKind.XUnitFilter => "Convert a playlist to xUnit method filters",
                    InputKind.VSTestFilter => "Convert a playlist to a VSTest filter",
                    _ => throw new ArgumentOutOfRangeException(nameof(value)),
                })
                .AddChoices(
                    InputKind.Number,
                    InputKind.FindAllReferences,
                    InputKind.XUnitFilter,
                    InputKind.VSTestFilter));
    }

    private static FindAllReferencesSource GetFindAllReferencesSource(PlaylistSettings settings)
    {
        if (settings.Clipboard)
        {
            return FindAllReferencesSource.Clipboard;
        }

        if (settings.Paste)
        {
            return FindAllReferencesSource.Paste;
        }

        return AnsiConsole.Prompt(
            new SelectionPrompt<FindAllReferencesSource>()
                .Title("How should find-all-references output be read?")
                .UseConverter(static value => value switch
                {
                    FindAllReferencesSource.Clipboard => "From the clipboard",
                    FindAllReferencesSource.Paste => "Paste into the terminal",
                    _ => throw new ArgumentOutOfRangeException(nameof(value)),
                })
                .AddChoices(FindAllReferencesSource.Clipboard, FindAllReferencesSource.Paste));
    }

    private static async Task<int> ProcessFindAllReferencesAsync(
        string outputDirectory,
        FindAllReferencesSource source)
    {
        string? clipboardContent = null;
        if (source == FindAllReferencesSource.Clipboard)
        {
            try
            {
                clipboardContent = await ClipboardService.GetTextAsync();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Could not read the clipboard:[/] {Markup.Escape(ex.Message)}");
                return -1;
            }

            if (string.IsNullOrEmpty(clipboardContent))
            {
                AnsiConsole.MarkupLine("[red]The clipboard does not contain any text.[/]");
                return -1;
            }
        }

        var playlistFileName = Path.Combine(
            outputDirectory,
            $"far-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.playlist");
        AnsiConsole.WriteLine($"Writing playlist file: {playlistFileName}");

        using var playlistWriter = File.CreateText(playlistFileName);
        playlistWriter.WriteLine("<Playlist Version=\"2.0\"><Rule Match=\"Any\">");
        var seenFileNames = new HashSet<string>(StringComparer.Ordinal);

        if (source == FindAllReferencesSource.Clipboard)
        {
            using var reader = new StringReader(clipboardContent!);
            for (string? line; (line = reader.ReadLine()) is not null;)
            {
                ProcessFindAllReferencesLine(line, seenFileNames, playlistWriter);
            }
        }
        else
        {
            AnsiConsole.WriteLine("Paste find-all-references output, then press Ctrl-Z or Ctrl-D to finish:");
            for (string? line; (line = Console.ReadLine()) is not null;)
            {
                ProcessFindAllReferencesLine(line, seenFileNames, playlistWriter);
            }
        }

        playlistWriter.WriteLine("</Rule></Playlist>");
        AnsiConsole.WriteLine($"Wrote playlist file: {playlistFileName}");
        return 0;
    }

    private static void ProcessFindAllReferencesLine(
        string line,
        HashSet<string> seenFileNames,
        StreamWriter playlistWriter)
    {
        // Example line:
        //   D:\roslyn-C\src\Compilers\CSharp\Portable\Binder\Semantics\OverloadResolution\OverloadResolutionResult.cs(1521):return new DiagnosticInfoWithSymbols(ErrorCode.ERR_AmbigCall, [distinguisher.First, distinguisher.Second], symbols);
        if (Helpers.FileNamePattern.Match(line) is { Success: true } match)
        {
            var fileName = match.Groups[1].Value;
            if (seenFileNames.Add(fileName))
            {
                AnsiConsole.WriteLine($"  File: {fileName}");
                playlistWriter.WriteLine($"<Property Name=\"Class\" Value=\"{Path.GetFileNameWithoutExtension(fileName)}\" />");
            }
        }
    }

    private static async Task<int> ProcessBuildAsync(
        string outputDirectory,
        int number,
        CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine($"PR number: {number}");

        var baseUrl = new Uri("https://dev.azure.com/dnceng-public");
        var project = "public";
        var connection = new VssConnection(baseUrl, new VssCredentials());
        var buildClient = connection.GetClient<BuildHttpClient>();
        var builds = await buildClient.GetBuildsAsync2(
            project: project,
            definitions: [95], // roslyn-CI
            branchName: $"refs/pull/{number}/merge",
            top: 1,
            cancellationToken: cancellationToken);
        Build? build;
        string playlistFileNamePrefix = "";
        if (builds.Count != 0)
        {
            build = builds[0];
            playlistFileNamePrefix = $"{number}-";
        }
        else
        {
            AnsiConsole.WriteLine("No builds found.");

            // Try build ID next.
            AnsiConsole.WriteLine($"Build ID: {number}");

            build = await buildClient.GetBuildAsync(
                project: project,
                buildId: number,
                cancellationToken: cancellationToken);

            if (build is null)
            {
                AnsiConsole.MarkupLine("[red]Build not found.[/]");
                return -1;
            }
        }

        AnsiConsole.WriteLine($"Build number: {build.BuildNumber}");

        var artifacts = await buildClient.GetArtifactsAsync(
            project: project,
            buildId: build.Id,
            cancellationToken: cancellationToken);
        var testLogArtifacts = artifacts
            .Select(static artifact => (Artifact: artifact, TestLegName: TryGetTestLegName(artifact.Name)))
            .Where(static item => item.TestLegName is not null);

        var playlistFileName = Path.Combine(
            outputDirectory,
            $"{playlistFileNamePrefix}{build.BuildNumber}.playlist");
        StreamWriter? playlistWriter = null;
        var seenTestNames = new HashSet<string>();
        using var client = new HttpClient();

        foreach (var (artifact, testLegName) in testLogArtifacts)
        {
            AnsiConsole.WriteLine($"Leg: {testLegName}");

            var files = await buildClient.GetFileAsync(
                project: project,
                buildId: build.Id,
                artifactName: artifact.Name,
                fileId: artifact.Resource.Data,
                fileName: string.Empty,
                cancellationToken: cancellationToken)
                //.DebugAsync()
                .ReadFromJsonAsync<ArtifactFiles>();

            var logFile = files.Items.FirstOrDefault(static file => file.Path == "/helix.binlog");

            if (logFile is null)
            {
                AnsiConsole.WriteLine("No log file found.");
                continue;
            }

            using var logStream = await buildClient.GetFileAsync(
                project: project,
                buildId: build.Id,
                artifactName: artifact.Name,
                fileId: logFile.Blob.Id,
                fileName: logFile.Path,
                cancellationToken: cancellationToken);

            var failureLogUrls = new List<string>();
            var logReader = new BinaryLogReplayEventSource();
            logReader.AnyEventRaised += (_, args) =>
            {
                // Example message: Work item workitem_0 in job <GUID> has failed.\nFailure log: https://helix.dot.net/api/.../console
                if (args is BuildErrorEventArgs error &&
                    !string.IsNullOrEmpty(error.Message) &&
                    Helpers.FailureLogPattern.Match(error.Message) is { Success: true } failureLogMatch)
                {
                    failureLogUrls.Add(failureLogMatch.Groups[1].Value);
                }
            };

            try
            {
                logReader.Replay(logStream, cancellationToken);
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteLine($"  {ex.GetType()}: {ex.Message}");
            }

            foreach (var failureLogUrl in failureLogUrls)
            {
                var failureLogContent = await client.GetStringAsync(failureLogUrl, cancellationToken);

                // Example line: [xUnit.net 00:00:23.67]     Some.Namespace.Test_Name(theory: "parameters") [FAIL]
                foreach (var testNameMatch in Helpers.TestNamePattern.Matches(failureLogContent).Cast<Match>())
                {
                    var testName = testNameMatch.Groups[1].Value;
                    if (seenTestNames.Add(testName))
                    {
                        AnsiConsole.WriteLine($"  Test: {testName}");

                        if (playlistWriter is null)
                        {
                            playlistWriter = File.CreateText(playlistFileName);
                            playlistWriter.WriteLine("<Playlist Version=\"2.0\"><Rule Match=\"Any\">");
                        }

                        playlistWriter.WriteLine($"<Property Name=\"TestWithNormalizedFullyQualifiedName\" Value=\"{testName}\" />");
                    }
                }
            }
        }

        if (playlistWriter is not null)
        {
            playlistWriter.WriteLine("</Rule></Playlist>");
            playlistWriter.Close();
            AnsiConsole.WriteLine($"Wrote playlist file: {playlistFileName}");
        }

        return 0;
    }

    private static string? TryGetTestLegName(string artifactName)
    {
        var match = Helpers.TestArtifactNamePattern.Match(artifactName);
        return match.Success ? match.Groups[1].Value : null;
    }
}

enum OutputLocation
{
    Temp,
    Current,
    Custom,
}

enum InputKind
{
    Number,
    FindAllReferences,
    XUnitFilter,
    VSTestFilter,
}

enum FindAllReferencesSource
{
    Clipboard,
    Paste,
}

static partial class Helpers
{
    [GeneratedRegex("""Failure log: (.+)$""")]
    public static partial Regex FailureLogPattern { get; }

    [GeneratedRegex("""^Test_(.+) Attempt (\d+) Logs$""")]
    public static partial Regex TestArtifactNamePattern { get; }

    [GeneratedRegex("""^\[[^]]+\]\s+([^(\r\n]+).* \[FAIL\]\r?$""", RegexOptions.Multiline)]
    public static partial Regex TestNamePattern { get; }

    [GeneratedRegex("""[/\\]([^/\\(]+)\(""")]
    public static partial Regex FileNamePattern { get; }

    public static async Task<Stream> DebugAsync(this Task<Stream> streamTask)
    {
        var content = await streamTask.ReadAsStringAsync();
        Console.WriteLine(content);
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    public static async Task<string> ReadAsStringAsync(this Task<Stream> streamTask)
    {
        await using var stream = await streamTask;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public static async Task<T> ReadFromJsonAsync<T>(this Task<Stream> streamTask)
    {
        await using var stream = await streamTask;
        var result = await JsonSerializer.DeserializeAsync<T>(stream, JsonSerializerOptions.Web);
        return result!;
    }
}

sealed class ArtifactFiles
{
    public required IReadOnlyList<ArtifactFile> Items { get; init; }
}

sealed class ArtifactFile
{
    public required string Path { get; init; }
    public required ArtifactFileBlob Blob { get; init; }
}

sealed class ArtifactFileBlob
{
    public required string Id { get; init; }
    public required long Size { get; init; }
}
