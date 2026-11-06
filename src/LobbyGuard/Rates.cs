using System;
using System.Collections.Generic;

namespace LobbyGuard;

// Packets and messages each sender sent in the current second, and the busiest second each has sent so far.
internal sealed class Rates
{
    private readonly Dictionary<ulong, (float Start, int Packets, int Messages)> windows = new();
    private readonly Dictionary<ulong, (int Packets, int Messages)> peaks = new();

    // Counts one packet of `messages` messages from id at `now` seconds and returns this second's totals so far.
    // `peak` is the new record when the second that just closed beat the sender's earlier ones.
    internal (int Packets, int Messages) Add(ulong id, int messages, float now, out (int Packets, int Messages)? peak)
    {
        peak = null;
        windows.TryGetValue(id, out (float Start, int Packets, int Messages) window);
        if (window.Packets > 0 && now - window.Start < 1)
        {
            window = (window.Start, window.Packets + 1, window.Messages + messages);
        }
        else
        {
            if (window.Packets > 0)
            {
                peak = Record(id, window.Packets, window.Messages);
            }
            window = (now, 1, messages);
        }
        windows[id] = window;
        return (window.Packets, window.Messages);
    }

    internal void Remove(ulong id)
    {
        windows.Remove(id);
        peaks.Remove(id);
    }

    private (int Packets, int Messages)? Record(ulong id, int packets, int messages)
    {
        peaks.TryGetValue(id, out (int Packets, int Messages) best);
        if (packets <= best.Packets && messages <= best.Messages)
        {
            return null;
        }
        best = (Math.Max(packets, best.Packets), Math.Max(messages, best.Messages));
        peaks[id] = best;
        return best;
    }
}
