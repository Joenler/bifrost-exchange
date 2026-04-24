using Xunit;

namespace Bifrost.Quoter.Tests.Integration;

/// <summary>
/// Marker collection for the Quoter integration tests. xUnit serializes
/// classes that share a CollectionDefinition, which is what we need:
/// every integration test drives a Quoter BackgroundService through a
/// FakeTimeProvider + PeriodicTimer combo, and several BackgroundService
/// loops competing for threadpool workers can starve the
/// PeriodicTimer.WaitForNextTickAsync continuations enough that the
/// captured-command counts diverge across same-seed determinism runs.
///
/// Disabling parallelization across integration test classes is a small
/// wall-clock cost (the suite is already in the seconds range) for a large
/// determinism win.
/// </summary>
[CollectionDefinition(nameof(IntegrationTestCollection), DisableParallelization = true)]
public sealed class IntegrationTestCollection
{
}
