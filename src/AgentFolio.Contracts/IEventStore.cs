namespace AgentFolio.Contracts;

/// <summary>Port: append-only event log (ADR-0005). No reads-for-update; rebuild is explicit.</summary>
public interface IEventStore
{
    /// <summary>Appends one event. Implementations must append BEFORE any projection mutation.</summary>
    Task AppendAsync(AppendOnlyEvent @event, CancellationToken ct = default);

    /// <summary>Streams all events in order for projection rebuild.</summary>
    IAsyncEnumerable<StoredEvent> RebuildAsync(CancellationToken ct = default);
}
