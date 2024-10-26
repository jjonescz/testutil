# testutil

Given a [Roslyn](https://github.com/dotnet/roslyn) PR number,
finds the set of tests failing in CI for that PR,
and produces a `.playlist` file which can be used in Visual Studio to run just the failing tests.

## Usage

```ps1
dotnet tool install -g testutil # install the tool
dotnet tool update -g testutil # update the tool if already installed
testutil <Roslyn GitHub PR number> # generated a .playlist file
```

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
dotnet pack -p:PackageVersion=<put-your-version-here>
```
