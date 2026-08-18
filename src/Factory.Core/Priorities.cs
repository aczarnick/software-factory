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
}
