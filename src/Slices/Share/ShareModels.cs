namespace Slices.Share;

/// <summary>View model for a public share page: the resolved tenant and its gate state.</summary>
public sealed record ShareView(string Slug, string TenantId, bool Gated);
