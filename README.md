# testutil

[![NuGet Downloads](https://img.shields.io/nuget/dt/testutil?logo=nuget&label=NuGet)](https://www.nuget.org/packages/testutil)

Given a [Roslyn](https://github.com/dotnet/roslyn) PR number (or AzDo build ID),
finds the set of tests failing in CI for that PR/build,
and produces a `.playlist` file which can be used in Visual Studio to run just the failing tests.

## Usage

```ps1
dotnet tool install -g testutil # install the tool
dotnet tool update -g testutil # update the tool if already installed

# Interactive: choose an operation and provide its inputs.
testutil

# Generate from a Roslyn GitHub PR number or Azure DevOps build ID.
testutil <number> --output current

# Generate from find-all-references output on the clipboard.
testutil --clipboard --output temp

# Paste find-all-references output into the terminal.
testutil --paste --output C:\playlists

# Convert a generated playlist to xUnit method filters.
testutil --xunit-filter tests.playlist

# Convert a generated playlist to a VSTest filter.
testutil --vstest-filter tests.playlist
```

`--output` accepts `temp`, `current`, or a directory path. Omit it to choose
interactively. `--clipboard` and `--paste` imply `--find-all-references`; when
only `--find-all-references` is provided, the input source is prompted for.
`--xunit-filter` writes repeated `-method` arguments suitable for the xUnit
runner to standard output.
`--vstest-filter` writes a `--filter` argument suitable for `dotnet test` or
VSTest to standard output.

## Related work

There's the great [runfo](https://github.com/jaredpar/runfo) tool
where you can do something similar via a command like
`runfo.exe search-tests -b <number> --playlist tests.playlist`.
However, the runfo tool requires authentication via PAT
(it has more functionality so it makes sense),
whereas testutil doesn't require any authentication,
so it's easier to use.

## Release process

```ps1
$version='<the next version here (without v prefix)>'
dotnet pack -p:PackageVersion=$version

# authenticate to nuget.org (only needed once)
winget install microsoft.nuge
nuget setapikey '<api key here>' -source https://api.nuget.org/v3/index.json

dotnet nuget push src/TestUtil/bin/Release/testutil.$version.nupkg --source https://api.nuget.org/v3/index.json
git tag v$version && git push origin v$version
```
