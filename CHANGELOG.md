# Changelog

## 0.2.5

- Closes a way for a player to change or crash your game. The game runs some messages meant only for clients even when
  a client sends them to the host, including ones that destroy objects, change who owns them and reset the game clock.
  Anyone who sends one now gets disconnected.
- Players are disconnected for asking to join a second time, or for sending more than a few messages before they've
  been accepted. Those messages also filled the log.
- A disconnected player's last messages are thrown away. A join request sent just before the kick could add them back.

## 0.2.4

- LobbyGuard protects your lobby again with mods that support other mods you don't have. Plenty of mods do this, like
  UniversalRadar with LethalLevelLoader. LobbyGuard hit an error looking through those at startup and quit before it
  set up its join and voice checks, so your lobby only had its packet limit. The same error also stopped some of those
  mods from loading, like LobbyImprovements without LobbyCompatibility.

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
