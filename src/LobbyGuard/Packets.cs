using System;
using System.Collections.Generic;
using System.Text;

namespace LobbyGuard;

// Reads the voice chat messages that players send to each other through the host, from just after the session ID, so
// the checks run without the game. Numbers are big-endian, and a string is its byte length plus one, or 0 for null.
internal static class Packets
{
    private static readonly HashSet<ushort> seen = new();

    // The sender has to be who sent it, and players only read text sent to a room (0) or a player (1).
    public static string? Text(ReadOnlySpan<byte> body, ushort sender)
    {
        int at = 5;
        if (body.Length < at || !String(body, ref at, out _))
        {
            return "broken voice packet";
        }
        if (body[0] > 1)
        {
            return "voice text to nobody";
        }
        return UInt16(body, 1) == sender ? null : "voice text sent as another player";
    }

    // The host passes this on to every player, and a missing name or room throws on each of them.
    public static string? ClientState(ReadOnlySpan<byte> body, ushort id, string name)
    {
        int at = 0;
        if (!String(body, ref at, out string? claimed) || claimed == null || body.Length - at < 13)
        {
            return "broken voice packet";
        }
        if (claimed != name || UInt16(body, at) != id)
        {
            return "voice state for another player";
        }
        if (!Voice.Allowed(body[at + 2], UInt32(body, at + 3), (int)UInt32(body, at + 7)))
        {
            return "bad voice codec settings";
        }
        int rooms = UInt16(body, at + 11);
        at += 13;
        for (int i = 0; i < rooms; i++)
        {
            if (!String(body, ref at, out string? room) || room == null)
            {
                return "broken voice packet";
            }
        }
        return null;
    }

    public static string? DeltaChannelState(ReadOnlySpan<byte> body, ushort id)
    {
        int at = 3;
        if (body.Length < at || !String(body, ref at, out string? room) || room == null)
        {
            return "broken voice packet";
        }
        return UInt16(body, 1) == id ? null : "voice state for another player";
    }

    // The host sends a copy to each player named, so naming one twice multiplies what it sends. The game never does.
    public static bool Repeats(List<ushort> players)
    {
        seen.Clear();
        foreach (ushort player in players)
        {
            if (!seen.Add(player))
            {
                return true;
            }
        }
        return false;
    }

    private static ushort UInt16(ReadOnlySpan<byte> p, int at) => (ushort)(p[at] << 8 | p[at + 1]);

    private static uint UInt32(ReadOnlySpan<byte> p, int at) => (uint)(p[at] << 24 | p[at + 1] << 16 | p[at + 2] << 8 | p[at + 3]);

    private static bool String(ReadOnlySpan<byte> p, ref int at, out string? value)
    {
        value = null;
        if (p.Length - at < 2)
        {
            return false;
        }
        int length = UInt16(p, at);
        at += 2;
        if (length == 0)
        {
            return true;
        }
        if (length - 1 > p.Length - at)
        {
            return false;
        }
        value = Encoding.UTF8.GetString(p.Slice(at, length - 1));
        at += length - 1;
        return true;
    }
}
