namespace Slices.Stars.Graph;

/// <summary>
/// GraphFusion Cypher builder (#70 B4): idempotent MERGE-style upserts for the
/// agentfolio_stars graph. Pure logic (testable without a live GF); the client
/// ships the Cypher over gf-a2a :50052 (tailscale) at orchestration time.
/// Node kinds: Repo, Chunk. Edges: has_chunk, next, mentions_repo.
/// Cypher braces ({}) conflict with C# interpolation — string.Create or
/// concatenation is clearer than multi-$ raw strings here.
/// </summary>
public static class StarsGraphCypher
{
    public static string UpsertRepo(long repoId, string fullName, string? description, string? language) =>
        "MERGE (r:Repo {repoId: " + repoId + "})" +
        " SET r.fullName = " + Q(fullName) + ", r.description = " + Q(description) +
        ", r.language = " + Q(language);

    public static string UpsertChunk(long repoId, int idx, string text, string headingPath) =>
        "MERGE (c:Chunk {chunkId: '" + repoId + ":" + idx + "'})" +
        " SET c.text = " + Q(text) + ", c.headingPath = " + Q(headingPath) + ", c.repoId = " + repoId +
        " WITH c MATCH (r:Repo {repoId: " + repoId + "})" +
        " MERGE (r)-[:has_chunk]->(c)";

    public static string LinkNextChunk(long repoId, int idx) =>
        "MATCH (a:Chunk {chunkId: '" + repoId + ":" + (idx - 1) + "'}), " +
        "(b:Chunk {chunkId: '" + repoId + ":" + idx + "'})" +
        " MERGE (a)-[:next]->(b)";

    public static string LinkMention(long fromRepoId, long toRepoId) =>
        "MATCH (a:Repo {repoId: " + fromRepoId + "}), (b:Repo {repoId: " + toRepoId + "})" +
        " MERGE (a)-[:mentions_repo]->(b)";

    /// <summary>Single-quote Cypher string literal, escaped.</summary>
    internal static string Q(string? s) =>
        s is null ? "null" : "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
