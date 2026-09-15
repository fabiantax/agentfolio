namespace AgentFolio.Contracts;

/// <summary>
/// Append-only domain event (ADR-0005 event-lite sourcing).
/// Payload is opaque JSON at the port level; interpretation belongs to slices.
/// </summary>
public sealed record AppendOnlyEvent(
    string Type,
    string PayloadJson,
    DateTimeOffset OccurredAt);

/// <summary>
/// Persisted shape of an appended event as read back from the store.
/// </summary>
public sealed record StoredEvent(
    long Id,
    string Type,
    string PayloadJson,
    DateTimeOffset OccurredAt);
