using Xunit;

namespace Bifrost.McConsole.Tests;

public sealed class McConsoleStartsTest
{
    [Fact]
    public void McConsole_Assembly_Loads()
    {
        // The mc-console binary has no Host; this test only proves the csproj compiles
        // and the test project's ProjectReference to Bifrost.McConsole resolves. Phase 06b
        // replaces this with a real CLI-command assertion.
        Assert.True(true);
    }
}
