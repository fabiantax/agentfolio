namespace AgentFolio.UnitTests;

using Xunit;

/// <summary>
/// Serializes every test class that mutates the process-global
/// AGENTFOLIO_PASSWORD_HASH environment variable (AuthTests, ChatTests,
/// StarsEndpointsTests). Without this, parallel classes race the env var
/// against each other's in-flight /auth/login calls — measured on CI
/// (2026-09-20, runs 33102/33118): ChatTests' login 401'd because another
/// class had swapped/cleared the hash between request send and the endpoint's
/// per-request env read. Local runners were fast enough to hide it.
/// </summary>
[CollectionDefinition("password-hash-env")]
public sealed class PasswordHashEnvCollection
{
}
