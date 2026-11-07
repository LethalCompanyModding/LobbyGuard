using System.Collections.Generic;

namespace LobbyGuard;

// How long each kicked player stays out. The first kick is a minute, and every further kick of the same player
// doubles it.
internal sealed class Cooldowns
{
    internal const double FirstSeconds = 60;

    // Past this the wait is far longer than any lobby lasts, and doubling again would overflow.
    private const int MaxDoublings = 30;

    private readonly Dictionary<ulong, (int Kicks, double Until)> kicked = new();

    // Records a kick of id at `now` seconds and returns how long they're kept out.
    internal double Add(ulong id, double now)
    {
        kicked.TryGetValue(id, out (int Kicks, double Until) seen);
        double wait = FirstSeconds * (1L << System.Math.Min(seen.Kicks, MaxDoublings));
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
