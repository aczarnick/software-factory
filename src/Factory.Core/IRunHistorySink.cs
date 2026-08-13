namespace Factory.Core;

/// <summary>An additional, best-effort destination for events — a tracing backend or
/// evaluator. Deliberately write-only: a sink receives traces, it never answers the
/// factory's queries, so an unreachable sink can never block a read.</summary>
public interface IRunHistorySink
{
    void Emit(FactoryEvent evt);
    void Flush();
}
