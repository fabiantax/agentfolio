namespace Slices.Stars.Retrieval;

/// <summary>
/// One enrichable repo row loaded from the tenant's stars_raw store (the hay the
/// stage-C A/B winner reranked over: description + topics + use_for + problems).
/// </summary>
public sealed record EnrichedRepo(
    long RepoId,
    string FullName,
    string? Description,
    string? TopicsJson,
    string? UseFor,
    string? ProblemsJson);

/// <summary>
/// The stage-C A/B WINNER arm D recall (2026-09-29, re-measured W2 09-30: 9/10 vs
/// 7/10 desc-only): keyword recall-30 over ENRICHED docs held in the tenant's own
/// stars_raw table, scored by DISTINCT-KEYWORD-HIT COUNT with the HARNESS TOKENIZER
/// VERBATIM (whitespace split, len>3, no stopword removal, no dedupe — 'ci/cd'
/// stays ONE term; splitting it loses the query, measured 09-30).
///
/// Why this exists (preview-gate #123, measured 2026-10-10): GF graph Repo nodes
/// carry NO description properties (sidecar ingest shipped fullName+repoId only),
/// so the Repo-arm keyword recall starves — woodpecker-ci/woodpecker is absent
/// from its own top-30 — and the deployed chat evaluated 3/10 strict / 4/10
/// lenient vs the ≥9 pre-registered bar. Hits carry RerankText so the existing
/// ColBERT reranker scores the enriched doc (the winner's exact shape) while the
/// citation snippet stays the description (attribution contract).
///
/// Owner-directive note (2026-09-28 "ALL retrieval through GF"): GF stays the
/// GRAPH surface (Bm25 + Repo arms remain as fallbacks downstream of this
/// provider); this arm adds the tenant's own store as the enrichment hay the
/// winner actually measured. No graph mutations. Flagged for owner veto in
/// preview-gate #123.
/// </summary>
public sealed class EnrichedRecallProvider
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<EnrichedRepo>>> _loader;
    private readonly TimeSpan _ttl;
    private readonly object _lock = new();
    private IReadOnlyList<EnrichedRepo>? _cache;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private bool _loadFailed;

    /// <param name="loader">Loads all enriched rows from the deployment's stars store.</param>
    /// <param name="ttl">Cache lifetime; the corpus grows by backfill fires, not per request.</param>
    public EnrichedRecallProvider(
        Func<CancellationToken, Task<IReadOnlyList<EnrichedRepo>>> loader,
        TimeSpan? ttl = null)
    {
        _loader = loader;
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Keyword recall over enriched docs, best-first, top <paramref name="topN"/>.
    /// Returns empty (never throws past the loader boundary — degrade-open, GF
    /// fallback downstream) when disabled, unpopulated, or no keyword matches.
    /// </summary>
    public async Task<IReadOnlyList<RetrievalHit>> RecallAsync(
        string query, int topN, CancellationToken ct = default)
    {
        var repos = await GetReposAsync(ct).ConfigureAwait(false);
        if (repos.Count == 0) return [];

        var kws = HarnessKeywords(query);
        if (kws.Count == 0) return [];

        var scored = new List<(int Score, EnrichedRepo Repo)>(repos.Count);
        foreach (var r in repos)
        {
            var hay = EnrichedHay(r);
            var s = 0;
            foreach (var k in kws)
                if (hay.Contains(k)) s++;
            if (s > 0) scored.Add((s, r));
        }

        // Stable tiebreak by RepoId: deterministic order for a given corpus state.
        scored.Sort((a, b) => b.Score != a.Score ? b.Score - a.Score : a.Repo.RepoId.CompareTo(b.Repo.RepoId));

        var hits = new List<RetrievalHit>(Math.Min(topN, scored.Count));
        foreach (var (score, repo) in scored.Take(topN))
        {
            hits.Add(new RetrievalHit(
                NodeId: $"repo:{repo.RepoId}",
                NodeKind: "Repo",
                Text: repo.Description ?? repo.FullName, // citation snippet (attribution)
                Score: score,
                RepoId: repo.RepoId,
                RepoFullName: repo.FullName)
            {
                RerankText = EnrichedDoc(repo), // the A/B winner's rerank surface
            });
        }
        return hits;
    }

    private async Task<IReadOnlyList<EnrichedRepo>> GetReposAsync(CancellationToken ct)
    {
        DateTimeOffset loadedAt;
        bool loadFailed;
        lock (_lock)
        {
            if (_cache is not null && DateTimeOffset.UtcNow - _loadedAt < _ttl)
                return _cache;
            loadedAt = _loadedAt;
            loadFailed = _loadFailed;
        }
        // Loader runs OUTSIDE the lock (it does IO).
        if (_cache is null || DateTimeOffset.UtcNow - loadedAt >= _ttl)
        {
            if (loadFailed && DateTimeOffset.UtcNow - loadedAt < _ttl) return _cache ?? [];
            try
            {
                var rows = await _loader(ct).ConfigureAwait(false);
                lock (_lock)
                {
                    _cache = rows;
                    _loadedAt = DateTimeOffset.UtcNow;
                    _loadFailed = false;
                }
            }
            catch (Exception e) when (e is IOException
                or System.Data.DataException
                or Microsoft.Data.Sqlite.SqliteException
                or OperationCanceledException
                or InvalidOperationException)
            {
                // Degrade-open: a store hiccup must not fail chat. Remember the
                // failure so we do not hammer a broken store on every request.
                lock (_lock)
                {
                    _loadFailed = true;
                    _loadedAt = DateTimeOffset.UtcNow;
                }
                return _cache ?? [];
            }
        }
        return _cache ?? [];
    }

    /// <summary>The hay the harness scored: description + topics + use_for + problems, lowercased.</summary>
    internal static string EnrichedHay(EnrichedRepo r)
    {
        return string.Join(' ', new[]
        {
            r.Description ?? "",
            NormalizeTopics(r.TopicsJson),
            r.UseFor ?? "",
            r.ProblemsJson ?? "",
        }).ToLowerInvariant();
    }

    /// <summary>
    /// The winner's rerank doc shape (harness `enriched_doc`): description +
    /// "use for: ..." + "problems: ..." + fullName. JSON-valued enrichment fields
    /// are compacted so the literal keeps its labels without bracket noise.
    /// </summary>
    internal static string EnrichedDoc(EnrichedRepo r)
    {
        static string Norm(string label, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = CompactJson(raw);
            return $"{label}: {raw}";
        }
        var parts = new[]
        {
            r.Description ?? "",
            Norm("use for", r.UseFor),
            Norm("problems", r.ProblemsJson),
        };
        var doc = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        doc = (doc + " " + r.FullName).Trim();
        return string.IsNullOrEmpty(doc) ? r.FullName : doc;
    }

    /// <summary>TopicsJson '["a","b"]' -> "a b"; plain text passes through.</summary>
    internal static string NormalizeTopics(string? topicsJson)
    {
        if (string.IsNullOrWhiteSpace(topicsJson)) return "";
        var t = topicsJson.Trim();
        if (t.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(t);
                var sb = new System.Text.StringBuilder();
                foreach (var el in doc.RootElement.EnumerateArray())
                    sb.Append(el.GetString()).Append(' ');
                return sb.ToString().Trim();
            }
            catch (System.Text.Json.JsonException)
            {
                // fall through: strip the brackets like the JSON path would have
                t = t.Trim('[', ']', '"');
            }
        }
        return t;
    }

    /// <summary>Compact a JSON array/object string to its scalar values ("a","b" -> "a b").</summary>
    private static string CompactJson(string raw)
    {
        var t = raw.Trim();
        if (t.StartsWith('[') || t.StartsWith('{'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(t);
                var sb = new System.Text.StringBuilder();
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var el in doc.RootElement.EnumerateArray())
                        sb.Append(el.ToString().Trim('"')).Append(' ');
                else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                        sb.Append(p.Value.ToString().Trim('"')).Append(' ');
                else
                    sb.Append(doc.RootElement.ToString());
                return sb.ToString().Trim();
            }
            catch (System.Text.Json.JsonException)
            {
                return t.Trim('[', ']', '{', '}', '"');
            }
        }
        return t;
    }

    /// <summary>
    /// THE HARNESS TOKENIZER, VERBATIM (w2_ab_harness.py keywords()): whitespace
    /// split, keep terms len&gt;3, NO stopword removal, NO dedupe. 'ci/cd' must
    /// stay ONE term — splitting on '/' drops it to len-2 fragments and loses the
    /// query (measured 2026-09-30).
    /// </summary>
    internal static IReadOnlyList<string> HarnessKeywords(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        return query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3)
            .ToList();
    }
}
