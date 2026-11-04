using System;
using System.Collections.Generic;

namespace LobbyGuard;

// What a client has sent while it waits to be accepted.
internal sealed class Joining
{
    public bool Requested;
    public int EarlyMessages;
}

// Checks a packet the way NGO's NetworkMessageManager.HandleIncomingData reads it, but strictly, so a bad one never
// gets there.
internal static class Batch
{
    private const ushort Magic = 0x1160;
    private const int HeaderSize = 16;

    // A mod on a real client can send a few early. NGO drops them and logs each one.
    public const int MaxEarlyMessages = 32;

    // The header is NGO's NetworkBatchHeader: ushort magic, ushort message count, int packet size, ulong hash of the
    // rest. The hash comes in from outside so this runs without the game. joining is null once the client is accepted.
    public static unsafe string? Check(ArraySegment<byte> data, Func<ArraySegment<byte>, ulong> hash, uint connectRequest,
        bool approval = true, Joining? joining = null, ISet<uint>? hostOnly = null)
    {
        if (data.Array == null || data.Count < HeaderSize)
        {
            return "packet too short";
        }
        fixed (byte* start = data.Array)
        {
            byte* p = start + data.Offset;
            if (*(ushort*)p != Magic || *(int*)(p + 4) != data.Count)
            {
                return "not a Netcode packet";
            }
            if (hash(new ArraySegment<byte>(data.Array, data.Offset + HeaderSize, data.Count - HeaderSize)) != *(ulong*)(p + 8))
            {
                return "packet hash doesn't match";
            }
            int pos = HeaderSize;
            for (int i = 0; i < *(ushort*)(p + 2); i++)
            {
                if (!Packed(p, data.Count, ref pos, out uint type) || !Packed(p, data.Count, ref pos, out uint size))
                {
                    return "bad message header";
                }
                if (size > (uint)(data.Count - pos))
                {
                    return "message bigger than its packet";
                }
                if (hostOnly != null && hostOnly.Contains(type))
                {
                    return "sent a message only the host can send";
                }
                if (type == connectRequest)
                {
                    if (joining == null || joining.Requested)
                    {
                        return "asked to join a second time";
                    }
                    joining.Requested = true;
                    if (approval && !ConnectionData(p, pos, pos + (int)size))
                    {
                        return "bad connection request";
                    }
                }
                else if (joining != null && ++joining.EarlyMessages > MaxEarlyMessages)
                {
                    return "too many messages before being accepted";
                }
                pos += (int)size;
            }
        }
        return null;
    }

    // ConnectionRequestMessage.Deserialize with approval on: a zigzag packed count of (uint hash, packed version) pairs,
    // a ulong config hash, then the connection data as an int length and that many bytes.
    private static unsafe bool ConnectionData(byte* p, int pos, int end)
    {
        if (!Packed(p, end, ref pos, out uint zigzag))
        {
            return false;
        }
        int versions = (int)(zigzag >> 1) ^ -(int)(zigzag & 1);
        for (int i = 0; i < versions; i++)
        {
            pos += 4;
            if (pos > end || !Packed(p, end, ref pos, out _))
            {
                return false;
            }
        }
        if (end - pos < 12)
        {
            return false;
        }
        int length = *(int*)(p + pos + 8);
        return length >= 0 && length <= end - pos - 12;
    }

    // NGO's ByteUnpacker.ReadValueBitPacked for a uint: the low 3 bits of the first byte give the length, 1 to 5.
    private static unsafe bool Packed(byte* p, int length, ref int pos, out uint value)
    {
        value = 0;
        if (pos >= length)
        {
            return false;
        }
        int n = p[pos] & 7;
        if (n == 0 || n > 5 || n > length - pos)
        {
            return false;
        }
        if (n == 5)
        {
            value = p[pos + 1] | (uint)p[pos + 2] << 8 | (uint)p[pos + 3] << 16 | (uint)p[pos + 4] << 24;
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                value |= (uint)p[pos + i] << (8 * i);
            }
            value >>= 3;
        }
        pos += n;
        return true;
    }
}
