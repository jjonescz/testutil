using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.FileContainer.Client;
using Microsoft.VisualStudio.Services.WebApi;

if (args.Length != 1 || !int.TryParse(args[0], out var prNumber) || prNumber < 0)
{
    Console.WriteLine("Usage: testutil <PR number>");
    return -1;
}

Console.WriteLine($"PR number: {prNumber}");

var baseUrl = new Uri("https://dev.azure.com/dnceng-public");
var project = "public";
var connection = new VssConnection(baseUrl, new VssCredentials());
var buildClient = connection.GetClient<BuildHttpClient>();
var containerClient = connection.GetClient<FileContainerHttpClient>();

var success = false;
//success |= await findInDefinitionAsync(95, "roslyn-CI");
success |= await findInDefinitionAsync(96, "roslyn-integration-CI");

return 0;

async Task<bool> findInDefinitionAsync(int definitionId, string pipelineName)
{
    Console.WriteLine($"Pipeline: {pipelineName}");

    var builds = await buildClient.GetBuildsAsync2(
        project: project,
        definitions: [definitionId],
        branchName: $"refs/pull/{prNumber}/merge",
        top: 1);
    if (builds.Count < 1)
    {
        Console.WriteLine("No builds found.");
        return false;
    }

    var build = builds[0];
    Console.WriteLine($"Build number: {build.BuildNumber}");

    var artifacts = await buildClient.GetArtifactsAsync(
        project: project,
        buildId: build.Id);
    var testLogArtifacts = artifacts
        .Select(static a => (Artifact: a, TestLegName: tryGetTestLegName(a.Name)))
        .Where(static t => t.TestLegName is not null);

    var playlistFileName = $"{prNumber}-{pipelineName}-{build.BuildNumber}.playlist";
    StreamWriter? playlistWriter = null;

    var seenTestNames = new HashSet<string>();

    using var client = new HttpClient();

    foreach (var (artifact, testLegName) in testLogArtifacts)
    {
        Console.WriteLine($"Leg: {testLegName}");

        var logContent = await downloadRuntestsLogAsync(artifact);

        if (logContent is null)
        {
            continue;
        }

        // The runtests.log might contain links to Helix or the failures themselves, so search for both.
        findInXUnitLog(logContent);

        // Example line: C:\...\Helix.targets(89,5): error : Failure log: https://helix.dot.net/api/.../console [D:\a\1\s\helix-tmp.csproj]
        foreach (var failureLogMatch in Helpers.FailureLogPattern.Matches(logContent).Cast<Match>())
        {
            var failureLogUrl = failureLogMatch.Groups[1].Value;
            //Console.WriteLine($" Failure log: {failureLogUrl}");

            var failureLogContent = await client.GetStringAsync(failureLogUrl.ToString());

            findInXUnitLog(failureLogContent);
        }
    }

    if (playlistWriter is not null)
    {
        playlistWriter.WriteLine("</Rule></Playlist>");
        playlistWriter.Flush();
        playlistWriter.Close();
        Console.WriteLine($"Wrote playlist file: {playlistFileName}");
    }

    return true;

    async Task<string?> downloadRuntestsLogAsync(BuildArtifact artifact)
    {
        const string path = "/runtests.log";

        if (artifact.Resource.Type == "Container")
        {
            return await containerClient.DownloadFileAsync(
                containerId: long.Parse(artifact.Resource.Data),
                itemPath: path,
                cancellationToken: default,
                scopeIdentifier: default)
                .ReadAsStringAsync();
        }

        var files = await buildClient.GetFileAsync(
            project: project,
            buildId: build.Id,
            artifactName: artifact.Name,
            fileId: artifact.Resource.Data,
            fileName: string.Empty)
            //.DebugAsync()
            .ReadFromJsonAsync<ArtifactFiles>();

        var logFile = files.Items.FirstOrDefault(static f => f.Path == path);

        if (logFile is null)
        {
            Console.WriteLine("No log file found.");
            return null;
        }

        return await buildClient.GetFileAsync(
            project: project,
            buildId: build.Id,
            artifactName: artifact.Name,
            fileId: logFile.Blob.Id,
            fileName: logFile.Path)
            //.DebugAsync()
            .ReadAsStringAsync();
    }

    void findInXUnitLog(string logContent)
    {
        // Example line: [xUnit.net 00:00:23.67]     Some.Namespace.Test_Name(theory: "parameters") [FAIL]
        foreach (var testNameMatch in Helpers.TestNamePattern.Matches(logContent).Cast<Match>())
        {
            var testName = testNameMatch.Groups[1].Value;
            if (seenTestNames.Add(testName))
            {
                Console.WriteLine($"  Test: {testName}");
                
                // Test playlist file format docs: https://learn.microsoft.com/en-us/visualstudio/test/run-unit-tests-with-test-explorer?view=vs-2022#create-custom-playlists
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

static string? tryGetTestLegName(string artifactName)
{
    var match = Helpers.TestArtifactNamePattern.Match(artifactName);
    if (match.Success)
    {
        return match.Groups[1].Value;
    }

    match = Helpers.IntegrationTestArtifactNamePattern.Match(artifactName);
    if (match.Success)
    {
        return match.Groups[2].Value;
    }

    return null;
}

static partial class Helpers
{
    [GeneratedRegex("""Failure log: (.+) \[""")]
    public static partial Regex FailureLogPattern { get; }

    [GeneratedRegex("""^Test_(.+) Attempt (\d+) Logs$""")]
    public static partial Regex TestArtifactNamePattern { get; }

    // Example: 1-Logs Release OOP64_False LspEditor_False 20241104.20
    [GeneratedRegex("""^(\d+)-Logs (.+)$""")]
    public static partial Regex IntegrationTestArtifactNamePattern { get; }

    [GeneratedRegex("""^\[[^]]+\]\s+([^(\r\n ]+).* \[FAIL\]\r?$""", RegexOptions.Multiline)]
    public static partial Regex TestNamePattern { get; }

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
