namespace Clementine.App.Wheel;

/// <summary>
/// Quando restare fermi sul mozzo deve passare all'anello successivo. La ruota si apre centrata sul cursore,
/// cioè sul mozzo: vale solo dopo esserne usciti una volta, sennò fermarsi a leggere cambia anello da solo.
/// </summary>
public sealed class HubDwell(TimeSpan dwell)
{
    bool _armed;
    DateTime? _since;

    /// <summary>All'apertura della ruota.</summary>
    public void Reset()
    {
        _armed = false;
        _since = null;
    }

    /// <summary>true quando è ora di cambiare anello (poi di nuovo a ogni intervallo, restando fermi).</summary>
    /// <param name="modifierHeld">Ctrl o Alt premuti: l'anello lo decide la tastiera, non il mozzo.</param>
    public bool Update(bool inHub, bool modifierHeld, DateTime now)
    {
        if (!inHub) _armed = true;
        if (!inHub || !_armed || modifierHeld)
        {
            _since = null;
            return false;
        }
        _since ??= now;
        if (now - _since < dwell) return false;
        _since = now;
        return true;
    }
}
