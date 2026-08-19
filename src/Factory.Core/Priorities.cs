namespace Factory.Core;

/// <summary>The dispatch priority band. Narrowed to the backlog store's 0-4 range so the store
/// and the factory agree on dispatch order rather than one silently rebucketing the other.</summary>
public static class Priorities
{
    public const int Highest = 0;
    public const int Default = 2;
    public const int Lowest = 4;

    /// <summary>One step less urgent than <paramref name="priority"/>, never leaving the band.
    /// Work a station files about work it was already doing sorts after its subject.</summary>
    public static int Below(int priority) => Math.Min(Lowest, priority + 1);

    /// <summary>Brings a value into the band, at the nearest edge. The band is the backlog store's,
    /// not a preference: bd refuses anything outside 0-4 with a non-zero exit, which
    /// <c>BeadsWorkItemStore.Write</c> raises as a halt — so an out-of-band value is a factory that
    /// stops, and stops again on every retry, rather than an item that sorts oddly.
    ///
    /// A clamp rather than a throw, because the values that reach it are historical: a ledger written
    /// before the band was narrowed replays through here, and a throw would turn a fold the factory
    /// used to write itself into a factory that cannot open at all.</summary>
    public static int Clamp(int priority) => Math.Clamp(priority, Highest, Lowest);
}
