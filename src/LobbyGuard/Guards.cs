using System;

namespace LobbyGuard;

internal static class Guards
{
    // One guard failing to hook shouldn't leave the rest off.
    public static bool Install(string name, Action install, Action<string> warn)
    {
        try
        {
            install();
            return true;
        }
        catch (Exception e)
        {
            warn($"Couldn't install {name}, so it's off: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }
}
