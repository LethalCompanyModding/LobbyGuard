# Changelog

## Unreleased

- Calls any player may make, which griefers abuse, are checked now. The host ignores a hit from far out of reach, a grab
  or use of an item someone else holds, selling or killing what the player doesn't own, a second vote to send the ship
  away, a purchase that takes the wrong amount of credits, and switches or chat faster than a person can use them.
  Acting as another player, negative credits, damage past 100 and calls the game never makes disconnect the sender.
  Enemy animation and state calls are capped too, so a script can't flood them. The call caps, hit reach and sale
  size are settings under "Rpc rules".
- If one protection can't be installed, LobbyGuard logs a warning saying which and why, and the rest still load.
- Claiming a huge list or text in any message, not just an RPC, no longer makes the host (or a player) set aside gigabytes
  of memory. The message is dropped, and a player who sent it to the host is disconnected. The checks now run in the
  game; before, a huge length in an RPC could still crash the host.
- A player LobbyGuard disconnects can't rejoin for a minute, and twice as long for each time it happens again in
  the same lobby.
- A `LogPeakRates` setting (off by default) logs each player's busiest second, in packets and messages.
- The host now checks the voice each player sends before passing it on. Anyone who sends voice the game can't play,
  or voice under another player's name, gets disconnected.
- Players can add LobbyGuard too. When a host without it passes on broken voice, they hear a moment of silence instead
  of an error.
- The host now checks everything players send each other through it. Anyone who sends text as someone else, sends a
  message only the host should (like one that takes a player out of voice chat), or names the same player twice gets
  disconnected.
- Players get disconnected for joining voice chat under another player's name, or for changing another player's voice
  settings.
- A player can't make the host set aside gigabytes of memory anymore by claiming a voice packet is bigger than it is.
- Players with LobbyGuard ignore voice settings the game never uses. A host without it passes those on, and they set
  the size of each player's voice buffers.

## 0.2.5

- Players can't destroy objects, change who owns them, reset the clock, load scenes or run the game's host-to-player
  calls in your game anymore, like ending the round. Only the host should send those, and anyone else who does gets
  disconnected.
- Players get disconnected for pretending to finish joining twice, for messages about many objects that don't exist,
  and for messages that keep causing errors.
- Players get disconnected for asking to join twice, or for sending more than a few messages before they're accepted.
- A join request sent just before a kick can't bring that player back.
- The log now says what was in the packet that got a player disconnected.

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
