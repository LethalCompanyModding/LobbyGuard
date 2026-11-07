using System.Collections.Generic;

namespace LobbyGuard;

// How long each kicked player stays out. The first kick is a minute, and every further kick of the same player
// doubles it, up to a limit.
internal sealed class Cooldowns
{
    private readonly Dictionary<ulong, (int Kicks, double Until)> kicked = new();

    // Records a kick of id at `now` seconds and returns how long they're kept out.
    internal double Add(ulong id, double now)
    {
        kicked.TryGetValue(id, out (int Kicks, double Until) seen);
        double wait = System.Math.Min(Limits.FirstWaitSeconds * System.Math.Pow(Limits.WaitMultiplier, seen.Kicks), Limits.MaxWaitSeconds);
        kicked[id] = (seen.Kicks + 1, now + wait);
        return wait;
    }

    // Seconds left before id may join again, or 0.
    internal double Remaining(ulong id, double now)
    {
        return kicked.TryGetValue(id, out (int Kicks, double Until) seen) && seen.Until > now ? seen.Until - now : 0;
    }

    internal void Clear()
    {
        kicked.Clear();
    }
}
