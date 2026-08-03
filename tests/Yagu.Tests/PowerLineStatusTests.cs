using Yagu.Helpers;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Smoke test for the <see cref="PowerLineStatus"/> P/Invoke wrapper (plan §6.1 IndexPauseOnBattery). The
/// actual value is machine-dependent (a desktop reports AC, a laptop on battery reports battery), so the
/// contract we can assert is only that the call succeeds without throwing and returns a bool — the helper
/// fails open, so a read failure is never surfaced.
/// </summary>
public sealed class PowerLineStatusTests
{
    [Fact]
    public void IsOnBattery_ReturnsWithoutThrowing()
    {
        bool onBattery = PowerLineStatus.IsOnBattery();
        Assert.True(onBattery || !onBattery); // tautology: it returned a bool, did not throw or hang
    }
}
