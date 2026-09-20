namespace Slices.Stars;

using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Full-fidelity, write-through snapshot of a completed stars sync, stored in the
/// tenant's object storage (S3). Purpose (user request 2026-09-17): a fresh
/// snapshot lets POST /stars/sync skip re-downloading every starred-repo page
/// from GitHub (measured: a first full sync of a large starred list takes
/// minutes), and preserves fields the shard projection drops (HtmlUrl, Stars,
/// Language, Owner, Topics). The cache is EXPLICIT, not a fallback: a stale or
/// missing snapshot always falls through to a real GitHub fetch, and a cache
/// write failure is reported in the response, never swallowed.
/// </summary>
public sealed record StarsSnapshot(
    DateTimeOffset SyncedAt,
    IReadOnlyList<StarredRepoDto> Repos)
{
    public static string KeyFor(string tenantId) => $"tenants/{tenantId}/stars/snapshot.json";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, JsonOpts);

    public static StarsSnapshot? Deserialize(string json) =>
        JsonSerializer.Deserialize<StarsSnapshot>(json, JsonOpts);
}
