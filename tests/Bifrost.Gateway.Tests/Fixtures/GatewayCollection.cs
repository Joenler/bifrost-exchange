using Xunit;

namespace Bifrost.Gateway.Tests.Fixtures;

/// <summary>
/// Serializes the in-process gateway tests so the recording publisher + mutable
/// round-state singleton fixtures don't race across concurrent test classes.
/// PATTERNS Wave 8 anti-pattern compliance: never share a port-bound test host
/// across parallel test methods (the WebApplicationFactory uses an in-memory
/// transport here, but the singleton fixture state is what we serialize against).
/// </summary>
[CollectionDefinition("Gateway", DisableParallelization = true)]
public sealed class GatewayCollection : ICollectionFixture<GatewayTestHost>
{
}
