# LobbyGuard

Keeps bad players from crashing or freezing your lobby.

- Who needs it: the host. Players can add it too, so broken voice from someone else plays as silence when the host
  doesn't have it.
- Config: none.

It checks what each player sends, voice chat included, before the game reads it. Anyone who sends something broken,
or far more than the game ever would, gets disconnected. Players you kick stay kicked.

Your lobby still shows as vanilla in the lobby list, so players who only look for vanilla lobbies can find it.

Each kick goes in the BepInEx log with the player's SteamID and the reason, so you can block them on Steam.

It can't do anything about a flood big enough to fill your internet connection.
