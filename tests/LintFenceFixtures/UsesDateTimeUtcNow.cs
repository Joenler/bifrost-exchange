// tests/LintFenceFixtures/UsesDateTimeUtcNow.cs
// EXPECTED: BannedApiAnalyzers emits RS0030 at the DateTime.UtcNow access.

using System;

namespace Bifrost.LintFenceFixtures;

public static class Violation_DateTimeUtcNow
{
    // BannedSymbols.txt: P:System.DateTime.UtcNow
    public static DateTime TimestampNow() => DateTime.UtcNow;
    //                                       ^^^^^^^^^^^^^^^ RS0030 expected
}
