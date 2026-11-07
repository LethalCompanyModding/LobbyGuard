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
