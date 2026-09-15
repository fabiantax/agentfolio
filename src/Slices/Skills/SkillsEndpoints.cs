using AgentFolio.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Slices.Skills;

/// <summary>HTTP endpoints for the derived skill taxonomy.</summary>
public static class SkillsEndpoints
{
    public static IEndpointRouteBuilder MapSkillsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/skills", async (IItemStore store, CancellationToken ct) =>
                Results.Json(await SkillsQuery.GetSkillsAsync(store, ct)))
            .WithName("GetSkills")
            .WithTags("Skills");

        return app;
    }
}
