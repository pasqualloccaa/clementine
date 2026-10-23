using Clementine.App.Wheel;
using Xunit;

namespace Clementine.Tests;

public class HubDwellTests
{
    static readonly DateTime T0 = new(2026, 1, 1);
    static DateTime At(int ms) => T0.AddMilliseconds(ms);

    static HubDwell Opened()
    {
        var hub = new HubDwell(TimeSpan.FromMilliseconds(800));
        hub.Reset();
        return hub;
    }

    [Fact]
    public void StayingStillOnOpen_NeverChangesRing() // il caso che trasformava "PDF" in uno ZIP
    {
        var hub = Opened();
        for (var ms = 0; ms <= 3000; ms += 50) Assert.False(hub.Update(inHub: true, modifierHeld: false, At(ms)));
    }

    [Fact]
    public void LeaveThenReturn_ChangesAfterDwell_ThenRepeats()
    {
        var hub = Opened();
        hub.Update(inHub: true, false, At(0));
        hub.Update(inHub: false, false, At(100)); // uscito: ora il mozzo è attivo
        Assert.False(hub.Update(inHub: true, false, At(200)));
        Assert.False(hub.Update(inHub: true, false, At(900)));
        Assert.True(hub.Update(inHub: true, false, At(1000)));
        Assert.False(hub.Update(inHub: true, false, At(1500)));
        Assert.True(hub.Update(inHub: true, false, At(1800))); // restando fermi gira ancora
    }

    [Fact]
    public void ModifierHeld_KeyboardDecides()
    {
        var hub = Opened();
        hub.Update(inHub: false, false, At(0));
        for (var ms = 100; ms <= 2000; ms += 100) Assert.False(hub.Update(inHub: true, modifierHeld: true, At(ms)));
    }

    [Fact]
    public void ReopeningWheel_DisarmsAgain()
    {
        var hub = Opened();
        hub.Update(inHub: false, false, At(0));
        hub.Reset(); // nuova ruota, di nuovo centrata sul cursore
        Assert.False(hub.Update(inHub: true, false, At(100)));
        Assert.False(hub.Update(inHub: true, false, At(2000)));
    }
}
