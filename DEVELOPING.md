# Developing LobbyGuard

## Commit hook

`lefthook.yml` runs gitleaks on staged files before each commit. Install [lefthook](https://lefthook.dev) and
[gitleaks](https://github.com/gitleaks/gitleaks), then run `lefthook install` once.

## Debugging

In the profile you test in, set `debug_enabled = true` in `doorstop_config.ini`, and `debug_suspend = true` if the game
should wait for the debugger at start-up. Start the game, then run Attach to Lethal Company in VS Code (Mono Debug
extension) or Rider (Mono Remote on 127.0.0.1:10000). Debug builds include symbols, so breakpoints work.

## Tests

Code that doesn't touch Unity can be tested outside the game. Put an xUnit v3 project under `tests/`, for example
`tests/LobbyGuard.Tests/LobbyGuard.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/LobbyGuard/LobbyGuard.csproj" />
    <PackageReference Include="xunit.v3" Version="4.0.2" />
  </ItemGroup>
</Project>
```

Add it to `LobbyGuard.slnx` and run `dotnet test`. CI runs it after the build. The plugin's classes derive from Unity
types, so tests reach the code through other classes, with `InternalsVisibleTo` on the plugin project for internal ones.

## Publishing

Pushing a `vX.Y.Z` tag publishes from CI with the `TCLI_AUTH_TOKEN` organization secret, once the build passes. To
publish from your own computer, create a service account token for the team on Thunderstore (Settings, Teams, Service
Accounts) and store it once:

- Linux: `secret-tool store --label "Thunderstore LethalCompanyModding" service thunderstore username LethalCompanyModding`,
  then paste the token.
- Windows: `Install-Module CredentialManager`, then
  `New-StoredCredential -Persist LocalMachine -Target 'thunderstore:LethalCompanyModding' -Password <token>`.

Then run `dotnet build -c Release -p:PublishTS=true`. Don't add `-bl` or `-v:d` to a publish build: MSBuild logs task
inputs, and the token is one.

## Live test

`tests/LobbyGuard.LiveTest` is a plugin for a real two-player run. Loaded on the host and the client, it sends the host
malformed payloads as if the client had sent them, checks that LobbyGuard kicked the client, and has the client rejoin.
Each case logs `LIVETEST <case> PASS|FAIL <detail>`, then a summary. It is never packaged or published.

Build it with LobbyGuard and DevLaunch, then run Mortar's sandbox from the mortar checkout (it copies the game twice,
under Proton on a hidden display with no network, and deletes the copies when the run passes):

```sh
dotnet build -c Release
export MORTAR_LAUNCH_SESSION=lobbyguard-live-1   # a launch uses two of the session's three
export MORTAR_LIVE_HOST_PLUGINS="Mortar-DevLaunch-X.zip artifacts/bin/LobbyGuard.LiveTest/release/*.livetest.dll artifacts/bin/LobbyGuard/release/com.github.lethalcompanymodding.lobbyguard.dll"
export MORTAR_LIVE_CLIENT_PLUGINS="Mortar-DevLaunch-X.zip artifacts/bin/LobbyGuard.LiveTest/release/*.livetest.dll"
scripts/selftest.sh live-lobby
```

Use absolute paths. It is a LAN game, so there are no SteamIDs and the rejoin cooldown does not apply.

To check the client's side of broken voice, leave LobbyGuard out of the host list and add
`MORTAR_LIVE_EXPECT_CLIENT='LIVETEST silence PASS'`: the host sends broken voice frames untouched, and the client
(which has LobbyGuard) has to log that it muted them.

### Fuzzing

Set `LIVETEST_FUZZ_SECONDS` and the host records the client's real packets, then sends mutants of them (and messages built
from NGO's types) at `LIVETEST_FUZZ_RATE` per second (default 20) instead of running the cases. Every kick is fine; an
exception without a kick, a receive over a second or a frozen frame (`LIVETEST_FUZZ_FREEZE`, default 2 s) is a failure,
logged with the mutant's hex and written to `BepInEx/livetest-fuzz-failures.log`. `livetest-fuzz-last.hex` holds the last
mutant if the host dies. `LIVETEST_FUZZ_SEED` and `LIVETEST_FUZZ_WARMUP` (seconds of recording, default 30) are optional.
Set `MORTAR_LIVE_TIMEOUT` to the run length plus a margin.
