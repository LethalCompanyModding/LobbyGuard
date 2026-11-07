using System;
using System.Collections.Generic;
using Xunit;

namespace LobbyGuard.Tests;

public sealed class GuardsTests
{
    [Fact]
    public void FailureIsWarnedWithItsReasonAndTheNextGuardStillInstalls()
    {
        List<string> warnings = [];
        bool laterRan = false;

        bool first = Guards.Install("voice checks", () => throw new InvalidOperationException("String conversion error"), warnings.Add);
        bool second = Guards.Install("packet limits", () => laterRan = true, warnings.Add);

        Assert.False(first);
        Assert.True(second);
        Assert.True(laterRan);
        string warning = Assert.Single(warnings);
        Assert.Contains("voice checks", warning);
        Assert.Contains("String conversion error", warning);
    }
}
