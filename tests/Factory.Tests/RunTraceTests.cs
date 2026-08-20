using Factory.Core;

namespace Factory.Tests;

/// <summary>
/// A run's session id is the only handle to what the agent actually did. The transport captures it,
/// the terminal result message carries it, and it was dropped before the ledger — leaving the
/// evolution loop to reason about prompts from scalars alone. Asked to improve a station on that
/// evidence, the optimiser correctly refused, saying any edit would be speculation.
/// </summary>
public class RunTraceTests
{
    [Fact]
    public void A_run_record_carries_the_session_that_produced_it()
    {
        var record = new RunRecord
        {
            RunId = "run_1",
            ItemId = "wi_1",
            StationId = "implement",
            SessionId = "e54c0e9a-9092-4051-9894-08d1d7561678"
        };

        Assert.Equal("e54c0e9a-9092-4051-9894-08d1d7561678", record.SessionId);
    }

    [Fact]
    public void A_session_id_survives_the_ledger_round_trip()
    {
        var record = new RunRecord
        {
            RunId = "run_1",
            ItemId = "wi_1",
            StationId = "implement",
            SessionId = "e54c0e9a-9092-4051-9894-08d1d7561678"
        };

        // Through the same serialiser the ledger writes with: a field that does not survive replay
        // is no better than one that was never recorded.
        var replayed = FactoryJson.Read<RunCompleted>(FactoryJson.Write(new RunCompleted(record)));

        Assert.Equal(record.SessionId, replayed!.Record.SessionId);
    }

    [Fact]
    public void A_run_with_no_session_reported_records_none()
    {
        var record = new RunRecord { RunId = "run_1", ItemId = "wi_1", StationId = "check" };

        // Deterministic stations never call a model, so there is no session to point at. Null is
        // the honest answer; an empty string would look like a handle that resolves to nothing.
        Assert.Null(record.SessionId);
    }
}
