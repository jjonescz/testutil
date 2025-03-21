﻿using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

Console.WriteLine($"Output directory: {Environment.CurrentDirectory}");

if (args.Length != 1 || !int.TryParse(args[0], out var arg) || arg < 0)
{
    Console.Write("PR number or build ID: ");
    if (Console.ReadLine() is not { Length: > 0 } input ||
        !int.TryParse(input, out arg) ||
        arg < 0)
    {
        Console.WriteLine("Not a valid positive integer.");
        return -1;
    }
}

Console.WriteLine($"PR number: {arg}");

var baseUrl = new Uri("https://dev.azure.com/dnceng-public");
var project = "public";
var connection = new VssConnection(baseUrl, new VssCredentials());
var buildClient = connection.GetClient<BuildHttpClient>();
var builds = await buildClient.GetBuildsAsync2(
    project: project,
    definitions: [95], // roslyn-CI
    branchName: $"refs/pull/{arg}/merge",
    top: 1);
Build? build;
string playlistFileNamePrefix = "";
if (builds.Count != 0)
{
    build = builds[0];
    playlistFileNamePrefix = $"{arg}-";
}
else
{
    Console.WriteLine("No builds found.");

    // Try build ID next.
    Console.WriteLine($"Build ID: {arg}");

    build = await buildClient.GetBuildAsync(
        project: project,
        buildId: arg);

    if (build is null)
    {
        Console.WriteLine("Build not found.");
        return -1;
    }
}

Console.WriteLine($"Build number: {build.BuildNumber}");

var artifacts = await buildClient.GetArtifactsAsync(
    project: project,
    buildId: build.Id);
var testLogArtifacts = artifacts
    .Select(static a => (Artifact: a, TestLegName: tryGetTestLegName(a.Name)))
    .Where(static t => t.TestLegName is not null);

var playlistFileName = $"{playlistFileNamePrefix}{build.BuildNumber}.playlist";
StreamWriter? playlistWriter = null;

var seenTestNames = new HashSet<string>();

using var client = new HttpClient();

foreach (var (artifact, testLegName) in testLogArtifacts)
{
    Console.WriteLine($"Leg: {testLegName}");

    var files = await buildClient.GetFileAsync(
        project: project,
        buildId: build.Id,
        artifactName: artifact.Name,
        fileId: artifact.Resource.Data,
        fileName: string.Empty)
        //.DebugAsync()
        .ReadFromJsonAsync<ArtifactFiles>();

    var logFile = files.Items.FirstOrDefault(static f => f.Path == "/helix.binlog");

    if (logFile is null)
    {
        Console.WriteLine("No log file found.");
        continue;
    }

    using var logStream = await buildClient.GetFileAsync(
        project: project,
        buildId: build.Id,
        artifactName: artifact.Name,
        fileId: logFile.Blob.Id,
        fileName: logFile.Path);

    var failureLogUrls = new List<string>();
    var logReader = new BinaryLogReplayEventSource();
    logReader.AnyEventRaised += (_, args) =>
    {
        // Example message: Work item workitem_0 in job <GUID> has failed.\nFailure log: https://helix.dot.net/api/.../console
        if (args is BuildErrorEventArgs error &&
            !string.IsNullOrEmpty(error.Message) &&
            Helpers.FailureLogPattern.Match(error.Message) is { Success: true } failureLogMatch)
        {
            var failureLogUrl = failureLogMatch.Groups[1].Value;
            failureLogUrls.Add(failureLogUrl);
            //Console.WriteLine($" Failure log: {failureLogUrl}");
        }
    };
    logReader.Replay(logStream, cancellationToken: default);

    foreach (var failureLogUrl in failureLogUrls)
    {
        var failureLogContent = await client.GetStringAsync(failureLogUrl.ToString());

        // Example line: [xUnit.net 00:00:23.67]     Some.Namespace.Test_Name(theory: "parameters") [FAIL]
        foreach (var testNameMatch in Helpers.TestNamePattern.Matches(failureLogContent).Cast<Match>())
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

if (playlistWriter is not null)
{
    playlistWriter.WriteLine("</Rule></Playlist>");
    playlistWriter.Flush();
    playlistWriter.Close();
    Console.WriteLine($"Wrote playlist file: {playlistFileName}");
}

return 0;

static string? tryGetTestLegName(string artifactName)
{
    var match = Helpers.TestArtifactNamePattern.Match(artifactName);
    return match.Success ? match.Groups[1].Value : null;
}

static partial class Helpers
{
    [GeneratedRegex("""Failure log: (.+)$""")]
    public static partial Regex FailureLogPattern { get; }

    [GeneratedRegex("""^Test_(.+) Attempt (\d+) Logs$""")]
    public static partial Regex TestArtifactNamePattern { get; }

    [GeneratedRegex("""^\[[^]]+\]\s+([^(\r\n]+).* \[FAIL\]\r?$""", RegexOptions.Multiline)]
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
