namespace Slices.Chat;

/// <summary>POST /chat request body.</summary>
public sealed record ChatRequest(string? ConversationId, string Message, string? TierHint);

/// <summary>A citation attached to a completed chat turn (populated by retrieval, later slice).</summary>
public sealed record ChatCitation(string Url, string Title, string? Snippet);

/// <summary>Final SSE event of a chat turn: conversation identity, resolved tier, citations.</summary>
public sealed record ChatCompletionEvent(string ConversationId, string Tier, IReadOnlyList<ChatCitation> Citations);

/// <summary>One streamed token chunk (SSE token event).</summary>
public sealed record ChatTokenEvent(string Text);
