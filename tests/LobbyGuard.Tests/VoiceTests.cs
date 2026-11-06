using Xunit;

namespace LobbyGuard.Tests;

public sealed class VoiceTests
{
    [Fact]
    public void OnlyTheGamesOwnCodecSettings()
    {
        Assert.True(Voice.Allowed(1, 960, 48000));
        Assert.True(Voice.Allowed(1, 2880, 48000));
        Assert.True(Voice.Allowed(0, 441, 44100));
        Assert.False(Voice.Allowed(1, 961, 48000));
        Assert.False(Voice.Allowed(1, uint.MaxValue, 48000));
        Assert.False(Voice.Allowed(1, 960, 1));
        Assert.False(Voice.Allowed(0, 100000, 44100));
        Assert.False(Voice.Allowed(2, 960, 48000));
    }

    [Fact]
    public void FramesMatchTheSendersSettings()
    {
        byte[] opus = [0xF8, .. new byte[60]];
        Assert.True(Voice.FrameAllowed(1, 960, opus));
        Assert.False(Voice.FrameAllowed(1, 480, opus));
        Assert.False(Voice.FrameAllowed(1, 960, [0xF9, 1, 2, 3]));
        Assert.False(Voice.FrameAllowed(1, 960, [0xF8, .. new byte[1100]]));
        Assert.True(Voice.FrameAllowed(0, 200, new byte[800]));
        Assert.False(Voice.FrameAllowed(0, 200, new byte[799]));
        Assert.False(Voice.FrameAllowed(0, 441, new byte[1764]));
        Assert.False(Voice.FrameAllowed(2, 960, opus));
    }
}
