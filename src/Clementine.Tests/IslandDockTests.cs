using System.Windows;
using Clementine.App.Island;
using Clementine.App.Settings;
using Xunit;

namespace Clementine.Tests;

public class IslandDockTests
{
    static readonly Rect Laptop = new(0, 0, 1536, 864);

    [Fact]
    public void OuterEdges_Dock()
    {
        Assert.Equal(DockSide.Right, IslandWindow.DockFor(1480, Laptop, screenOnLeft: false, screenOnRight: false));
        Assert.Equal(DockSide.Left, IslandWindow.DockFor(5, Laptop, screenOnLeft: false, screenOnRight: false));
        Assert.Equal(DockSide.None, IslandWindow.DockFor(700, Laptop, screenOnLeft: false, screenOnRight: false));
    }

    // il caso del video: bordo destro del portatile, con il secondo monitor subito dopo
    [Fact]
    public void EdgeBetweenTwoMonitors_DoesNotDock()
    {
        Assert.Equal(DockSide.None, IslandWindow.DockFor(1480, Laptop, screenOnLeft: false, screenOnRight: true));
        Assert.Equal(DockSide.None, IslandWindow.DockFor(5, Laptop, screenOnLeft: true, screenOnRight: false));
    }
}
