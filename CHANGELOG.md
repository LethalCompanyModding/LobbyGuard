# Changelog

## 0.2.4

- LobbyGuard's protections go in again when another mod ships support for a mod you don't have. LobbyGuard stopped
  loading partway through on those, leaving joins and voice unguarded, and could stop that other mod from loading too.

## 0.2.3

- LobbyGuard now lives at LethalCompanyModding on GitHub and Thunderstore. Its plugin ID is now
  `com.github.lethalcompanymodding.lobbyguard`, so it starts with a fresh config file.

## 0.2.2

- Kicked players stay kicked again, and the log names players by their real SteamID. LobbyGuard read every
  player's SteamID as 0, which is also why 0.2.0 turned everyone away.

## 0.2.1

- Players can join again. 0.2.0 turned every joining player away with "Your SteamID doesn't match your connection."
  Kicked players still stay kicked.

## 0.2.0

- Your lobby shows as vanilla in the lobby list, so players who only look for vanilla lobbies can find it.

## 0.1.0

- First release. The host disconnects players who send something broken or far too much, and players you kick stay
  kicked.
