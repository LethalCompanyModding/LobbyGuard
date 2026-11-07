using BepInEx.Configuration;

namespace LobbyGuard;

// The host's own limits. Each is read where it's used, so a change applies at once. Unbound (in tests), they're the defaults.
internal static class Limits
{
    private static ConfigEntry<int>? errors, deferred, packets, messages, early, connectionData, firstWait, multiplier, maxWait;

    // A player's messages shouldn't cause errors at all. This leaves room for a mod that's broken for them.
    internal static int MaxErrorsPerSecond => errors?.Value ?? 100;

    // A joining client's RPCs can wait a moment for their objects.
    internal static int MaxDeferredPerSecond => deferred?.Value ?? 500;

    // Netcode sends a client's packets once per channel per frame, so the default leaves room for a very high frame rate.
    internal static int MaxPacketsPerSecond => packets?.Value ?? 2000;

    // A packet can hold thousands of tiny messages.
    internal static int MaxMessagesPerSecond => messages?.Value ?? 20000;

    // A mod on a real client can send a few before the host accepts it.
    internal static int MaxEarlyMessages => early?.Value ?? 32;

    // The game sends its version and SteamID, and logs whatever it gets.
    internal static int MaxConnectionData => connectionData?.Value ?? 1024;

    internal static int FirstWaitSeconds => firstWait?.Value ?? 60;

    internal static int WaitMultiplier => multiplier?.Value ?? 2;

    internal static int MaxWaitSeconds => maxWait?.Value ?? 31536000;

    internal static void Bind(ConfigFile config)
    {
        errors = Int(config, "Limits", "MaxErrorsPerSecond", 100, 1, 10000,
            "Errors a player's messages may cause in a second before they're disconnected.");
        deferred = Int(config, "Limits", "MaxDeferredPerSecond", 500, 1, 100000,
            "Messages for objects that don't exist yet a player may send in a second before they're disconnected.");
        packets = Int(config, "Limits", "MaxPacketsPerSecond", 2000, 10, 1000000,
            "Packets a player may send in a second before they're disconnected.");
        messages = Int(config, "Limits", "MaxMessagesPerSecond", 20000, 10, 10000000,
            "Messages a player may send in a second before they're disconnected.");
        early = Int(config, "Limits", "MaxEarlyMessages", 32, 0, 1000,
            "Messages a player may send before the host accepts them.");
        connectionData = Int(config, "Limits", "MaxConnectionDataBytes", 1024, 64, 65536,
            "Largest join request the host accepts, in bytes.");
        firstWait = Int(config, "Kicks", "FirstWaitSeconds", 60, 0, 86400,
            "How long a disconnected player must wait before rejoining.");
        multiplier = Int(config, "Kicks", "WaitMultiplier", 2, 1, 10,
            "The wait grows by this much each time the same player is disconnected again. 1 keeps it the same.");
        maxWait = Int(config, "Kicks", "MaxWaitSeconds", 31536000, 1, 31536000,
            "The longest a disconnected player must wait.");
    }

    internal static ConfigEntry<int> Int(ConfigFile config, string section, string key, int value, int min, int max, string about)
    {
        return config.Bind(section, key, value, new ConfigDescription(about, new AcceptableValueRange<int>(min, max)));
    }
}
