using AgentFolio.Contracts;

namespace Slices.Skills;

/// <summary>CQRS query: materialize stars + saves and derive the skill taxonomy.</summary>
public static class SkillsQuery
{
    private const int Cap = 5000;

    public static async Task<IReadOnlyList<SkillStat>> GetSkillsAsync(IItemStore store, CancellationToken ct = default)
    {
        var stars = new List<StarredRepoRecord>(Cap);
        await foreach (var star in store.QueryStarsAsync(ct).WithCancellation(ct))
        {
            stars.Add(star);
            if (stars.Count >= Cap) break;
        }

        var saves = new List<SavedPostRecord>(Cap);
        await foreach (var save in store.QuerySavesAsync(includeStale: true, ct).WithCancellation(ct))
        {
            saves.Add(save);
            if (saves.Count >= Cap) break;
        }

        return SkillTaxonomy.Derive(stars, saves);
    }
}
