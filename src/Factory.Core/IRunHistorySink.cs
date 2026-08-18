namespace Factory.Core;

/// <summary>An additional, best-effort destination for events — a tracing backend or
/// evaluator. Deliberately write-only: a sink receives traces, it never answers the
/// factory's queries, so an unreachable sink can never block a read.
///
/// <see cref="Emit"/> may be called concurrently and implementations must be thread-safe:
/// the factory records from parallel station tasks. Events may also arrive out of durable-log
/// order, because the sequence number is assigned under the writer's lock and the sink is
/// offered the event outside it; a sink that needs the log's order sorts on
/// <see cref="FactoryEvent.Seq"/>.</summary>
public interface IRunHistorySink
{
    void Emit(FactoryEvent evt);
    void Flush();
}
