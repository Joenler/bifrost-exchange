// tests/LintFenceFixtures/UsesRandomShared.cs
// EXPECTED: BannedApiAnalyzers emits RS0030 at the Random.Shared access.

using System;

namespace Bifrost.LintFenceFixtures;

public static class Violation_RandomShared
{
    // BannedSymbols.txt: P:System.Random.Shared
    public static int RollDice() => Random.Shared.Next(1, 7);
    //                              ^^^^^^^^^^^^^ RS0030 expected
}
