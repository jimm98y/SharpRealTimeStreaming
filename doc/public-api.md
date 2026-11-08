# Public API

What SharpRTCP, SharpRTSPClient and SharpRTSPServer expose is written down, and a change to it does
not build until it is written down too. Two checks do this, both on every `dotnet build` of a
library:

- **The API files.** `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, next to each project,
  list every public type and member. Shipped is the API as released; Unshipped is what changed
  since. A public symbol missing from them is a warning, RS0016, until it is
  listed - an addition breaks no one - and a listed one that is gone fails the build with RS0017. Read them to see the API; read their diff in a pull request to see how it changed.
- **Package validation.** The package being built is compared with the last release
  (`PackageValidationBaselineVersion` in the project), and every binary or source breaking change
  fails the build with a CP error. It also checks that the four target frameworks offer the same API.

## Adding to the API

List what the RS0016 warnings name: accept their fix in the IDE ("Add to public API"), or from the command line:

```
dotnet format analyzers src/SharpRTSPClient/SharpRTSPClient.csproj --diagnostics RS0016 --severity warn
```

The new lines land in `PublicAPI.Unshipped.txt`. An addition is not a break, so package validation
stays quiet.

## Removing or changing the API

RS0017 names what disappeared. Moving its line from Shipped to Unshipped with a `*REMOVED*` prefix
records the removal; a member whose signature changed is a removal of the old line and an
addition of the new one.

If package validation then reports a CP error, the change breaks code built against the last
release. When that is intended, record it:

```
dotnet build src/SharpRTSPClient/SharpRTSPClient.csproj -c Release -p:ApiCompatGenerateSuppressionFile=true
```

That rewrites `CompatibilitySuppressions.xml` next to the project. Say in the pull request what
broke, and add it to [migration.md](migration.md).

Validation runs as part of the build, because the build packs. `dotnet pack` on its own does not
rebuild and validates whatever binaries are already in `bin`.

## Releasing

1. Move every line of each `PublicAPI.Unshipped.txt` into its `PublicAPI.Shipped.txt`, dropping
   the `*REMOVED*` lines together with the shipped lines they remove.
2. Once the release is on nuget.org, set `PackageValidationBaselineVersion` to it, add one to
   SharpRTCP (which has had no release to compare with), and delete the `CompatibilitySuppressions.xml`
   files: what they excused is in the baseline now.

## Against 0.8.0

The client and the server are compatible with 0.8.0 but for one change, recorded in
`src/SharpRTSPClient/CompatibilitySuppressions.xml`: `IStreamConfigurationData` gained `Fmtp`, which
a class of your own that implements it has to add (see [migration.md](migration.md)). What 0.8.0
had and is now done differently, such as `SharpRTSPServer.RTCPUtils`, is kept and marked obsolete
rather than removed.

Names keep the convention 0.8.0 had, acronyms in capitals (`RTSPClient`, `RTPStream`, `SSRC`), and
SharpRTCP follows it too (`RTCPUtilities`, `NTPTimestamp`).
