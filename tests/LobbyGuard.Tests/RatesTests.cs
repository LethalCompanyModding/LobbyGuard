using Xunit;

namespace LobbyGuard.Tests;

public sealed class RatesTests
{
    [Fact]
    public void CountsOneSecondAndReportsItsRecordOnceItCloses()
    {
        Rates rates = new();
        Assert.Equal((1, 10), rates.Add(1, 10, 0.0f, out (int Packets, int Messages)? peak));
        Assert.Null(peak);
        Assert.Equal((2, 15), rates.Add(1, 5, 0.5f, out peak));
        Assert.Null(peak);

        Assert.Equal((1, 3), rates.Add(1, 3, 1.2f, out peak));
        Assert.Equal((2, 15), peak);

        // A quieter second sets no record.
        rates.Add(1, 1, 2.5f, out peak);
        Assert.Null(peak);
    }

    [Fact]
    public void KeepsEachSenderApart()
    {
        Rates rates = new();
        rates.Add(1, 100, 0.0f, out _);
        Assert.Equal((1, 1), rates.Add(2, 1, 0.1f, out (int Packets, int Messages)? peak));
        Assert.Null(peak);
    }

    [Fact]
    public void ForgetsASenderThatLeft()
    {
        Rates rates = new();
        rates.Add(1, 100, 0.0f, out _);
        rates.Remove(1);
        Assert.Equal((1, 1), rates.Add(1, 1, 0.5f, out (int Packets, int Messages)? peak));
        Assert.Null(peak);
    }
}
