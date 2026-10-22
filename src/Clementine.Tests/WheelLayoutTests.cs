using Clementine.App.Wheel;
using Xunit;

namespace Clementine.Tests;

public class WheelLayoutTests
{
    [Theory]
    [InlineData(0, -100, 0)]   // alto
    [InlineData(100, 0, 1)]    // destra
    [InlineData(0, 100, 2)]    // basso
    [InlineData(-100, 0, 3)]   // sinistra
    [InlineData(60, -80, 0)]   // 36° dal nord: ancora nello spicchio 0 (±45°)
    [InlineData(80, -60, 1)]   // 53°: spicchio 1
    [InlineData(-60, -80, 0)]  // -36°: spicchio 0, non l'ultimo
    public void HitTest_FourSlices(double dx, double dy, int expected) =>
        Assert.Equal(expected, WheelLayout.HitTest(dx, dy, 30, 140, 4));

    [Theory]
    [InlineData(0, 10)]   // nel mozzo
    [InlineData(0, 150)]  // fuori dalla ruota
    public void HitTest_OutsideRing(double dx, double dy) =>
        Assert.Equal(-1, WheelLayout.HitTest(dx, dy, 30, 140, 4));
}
