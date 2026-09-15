using AgentFolio.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AgentFolio.Platform;

/// <summary>
/// EF Core 10 DbContext for AgentFolio on SQLite (WAL).
///
/// FTS5 note: EF Core has no native FTS5 mapping. The FTS5 virtual table
/// (note search index) is created via raw SQL in SqliteStoreBootstrap.EnsureSchemaAsync
/// and kept in sync with triggers on the Stars/Saves projection tables. Migrations
/// must never try to model `fts5` entities — always use Sql() there.
/// </summary>
public sealed class AgentFolioDbContext : DbContext
{
    public AgentFolioDbContext(DbContextOptions<AgentFolioDbContext> options) : base(options) { }

    public DbSet<StarredRepo> Stars => Set<StarredRepo>();
    public DbSet<SavedPost> Saves => Set<SavedPost>();
    public DbSet<ChatMessage> Chat => Set<ChatMessage>();
    public DbSet<StoredEventEntity> Events => Set<StoredEventEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // events: append-only log (ADR-0005). Id INTEGER PK autoincrement.
        b.Entity<StoredEventEntity>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Type).IsRequired();
            e.Property(x => x.Payload).HasColumnName("payload");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();
        });

        // stars projection: unique RepoId
        b.Entity<StarredRepo>(e =>
        {
            e.ToTable("stars");
            e.HasKey(x => x.RepoId);
            e.Property(x => x.RepoId).ValueGeneratedNever();
            e.HasIndex(x => x.RepoId).IsUnique();
        });

        // saves projection: unique CanonicalUrl
        b.Entity<SavedPost>(e =>
        {
            e.ToTable("saves");
            e.HasKey(x => x.CanonicalUrl);
            e.Property(x => x.CanonicalUrl).IsRequired();
            e.HasIndex(x => x.CanonicalUrl).IsUnique();
        });

        b.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.ConversationId);
        });
    }
}

public sealed class StoredEventEntity
{
    public long Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class StarredRepo
{
    public long RepoId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? TopicsJson { get; set; }
    public DateTimeOffset StarredAt { get; set; }
}

public sealed class SavedPost
{
    public string CanonicalUrl { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? TextExcerpt { get; set; }
    public bool IsStale { get; set; }
    public DateTimeOffset SavedAt { get; set; }
}

public sealed class ChatMessage
{
    public long Id { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}
