using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class ValueObjectTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IngressOrdinalRequiresPositiveValue(long value)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new KanataIngressOrdinal(value));

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void GeneratedIdentifiersAreNonEmpty()
    {
        Assert.NotEqual(Guid.Empty, KanataConnectionId.New().Value);
        Assert.NotEqual(Guid.Empty, TargetDiscoveryRequestId.New().Value);
    }
}
