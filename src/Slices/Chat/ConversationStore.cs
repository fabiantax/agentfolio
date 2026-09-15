using System.Runtime.CompilerServices;
using AgentFolio.Contracts;
using AgentFolio.Platform;
using Microsoft.EntityFrameworkCore;

namespace Slices.Chat;

/// <summary>Chat conversation persistence over the chat_messages projection table.</summary>
public interface IConversationStore
{
    Task StoreChatMessageAsync(string conversationId, string role, string text, CancellationToken ct = default);

    IAsyncEnumerable<StoredChatMessage> LoadConversationAsync(string conversationId, CancellationToken ct = default);
}

/// <summary>
/// Projection-only store (ADR-0005: chat turns are conversation history rows, not
/// domain events — no event-log append). Uses the existing ChatMessage entity.
/// </summary>
public sealed class SqliteConversationStore : IConversationStore
{
    private readonly string _connectionString;

    public SqliteConversationStore(string connectionString) => _connectionString = connectionString;

    public async Task StoreChatMessageAsync(string conversationId, string role, string text, CancellationToken ct = default)
    {
        await using var db = NewDbContext();
        db.Chat.Add(new ChatMessage
        {
            ConversationId = conversationId,
            Role = role,
            Content = text,
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    public async IAsyncEnumerable<StoredChatMessage> LoadConversationAsync(
        string conversationId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<ChatMessage> rows;
        await using (var db = NewDbContext())
        {
            rows = await db.Chat
                .Where(m => m.ConversationId == conversationId)
                .OrderBy(m => m.Id)
                .ToListAsync(ct);
        }

        foreach (var m in rows)
        {
            yield return new StoredChatMessage(m.Id, m.ConversationId, m.Role, m.Content, m.OccurredAt);
        }
    }

    private AgentFolioDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AgentFolioDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new AgentFolioDbContext(options);
    }
}
