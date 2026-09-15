namespace AgentFolio.Contracts;

/// <summary>
/// Read-back shape of one persisted chat message (projection-only; chat turns are
/// NOT appended to the event log — they are plain conversation history rows).
/// </summary>
public sealed record StoredChatMessage(
    long Id,
    string ConversationId,
    string Role,
    string Text,
    DateTimeOffset OccurredAt);
