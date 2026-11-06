using System;

namespace LobbyGuard;

// Reads an Opus packet's framing (RFC 6716 section 3) the way libopus's opus_packet_parse does. libopus refuses a
// packet only for its framing or its length, so one that passes here decodes without an error.
internal static class Opus
{
    private const int MaxFrameBytes = 1275;
    private const int MaxSamples = 5760;

    private static readonly int[] silkSamples = { 480, 960, 1920, 2880 };

    // How many samples at 48 kHz the packet decodes to, or -1 if libopus would refuse it.
    public static int Samples(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 1)
        {
            return -1;
        }
        int config = packet[0] >> 3;
        int perFrame = config < 12 ? silkSamples[config & 3] : config < 16 ? 480 << (config & 1) : 120 << (config & 3);
        ReadOnlySpan<byte> data = packet.Slice(1);
        int frames;
        int last;
        switch (packet[0] & 3)
        {
            case 0:
                frames = 1;
                last = data.Length;
                break;
            case 1:
                if ((data.Length & 1) != 0)
                {
                    return -1;
                }
                frames = 2;
                last = data.Length / 2;
                break;
            case 2:
                {
                    int bytes = Size(data, out int first);
                    if (bytes < 0 || first > data.Length - bytes)
                    {
                        return -1;
                    }
                    frames = 2;
                    last = data.Length - bytes - first;
                    break;
                }
            default:
                {
                    if (data.Length < 1)
                    {
                        return -1;
                    }
                    int header = data[0];
                    frames = header & 0x3F;
                    if (frames == 0 || perFrame * frames > MaxSamples)
                    {
                        return -1;
                    }
                    data = data.Slice(1);
                    int len = data.Length;
                    if ((header & 0x40) != 0)
                    {
                        int pad;
                        do
                        {
                            if (len <= 0)
                            {
                                return -1;
                            }
                            pad = data[0];
                            data = data.Slice(1);
                            len -= 1 + (pad == 255 ? 254 : pad);
                        }
                        while (pad == 255);
                        if (len < 0)
                        {
                            return -1;
                        }
                    }
                    if ((header & 0x80) != 0)
                    {
                        // Every frame's length but the last comes first, then the frames.
                        last = len;
                        for (int i = 0; i < frames - 1; i++)
                        {
                            int bytes = Size(data.Slice(0, len), out int size);
                            if (bytes < 0)
                            {
                                return -1;
                            }
                            len -= bytes;
                            if (size > len)
                            {
                                return -1;
                            }
                            data = data.Slice(bytes);
                            last -= bytes + size;
                        }
                        if (last < 0)
                        {
                            return -1;
                        }
                    }
                    else
                    {
                        last = len / frames;
                        if (last * frames != len)
                        {
                            return -1;
                        }
                    }
                    break;
                }
        }
        return last > MaxFrameBytes ? -1 : frames * perFrame;
    }

    // A frame length takes one byte below 252, else two.
    private static int Size(ReadOnlySpan<byte> data, out int size)
    {
        size = -1;
        if (data.Length < 1)
        {
            return -1;
        }
        if (data[0] < 252)
        {
            size = data[0];
            return 1;
        }
        if (data.Length < 2)
        {
            return -1;
        }
        size = 4 * data[1] + data[0];
        return 2;
    }
}
