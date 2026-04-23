using System.Diagnostics;
using Xunit;

namespace Bifrost.Contracts.Roundtrip.Tests;

/// <summary>
/// CONT-04: polyglot byte-equivalence round-trip for every canonical target.
///
/// For each (TypeName, canonicalBytes) pair from <see cref="CanonicalBuilders.EveryRoundtripTarget"/>:
///
///   1. Write the canonical C# bytes to a tempfile.
///   2. Invoke `uv run python contracts/roundtrip/harness.py --in &lt;tmpIn&gt;
///      --type &lt;TypeName&gt; --out &lt;tmpOut&gt;` with the repo root (located by
///      walking up to `Bifrost.sln`) as the process working directory.
///   3. Python parses the bytes + re-serialises back out (ParseFromString /
///      SerializeToString only — Pitfall E: never attribute-assign on nested
///      oneof fields).
///   4. Read the Python-emitted bytes and assert byte equality with the
///      canonical C# bytes.
///
/// Byte equality after a parse-and-re-emit closes both directions in a single
/// theory row: if Python can re-emit exactly what C# produced, then Python
/// emits messages that C# could also parse (and vice-versa) — the protobuf
/// wire format is deterministic under this scheme because proto3 implementations
/// on both sides write fields in ascending tag order.
///
/// D-09: no .pb fixture files are committed. Every test run builds the
/// canonical bytes at runtime from the current <see cref="CanonicalBuilders"/>.
/// </summary>
public sealed class RoundtripTheories
{
    public static IEnumerable<object[]> AllTargets() =>
        CanonicalBuilders.EveryRoundtripTarget()
            .Select(t => new object[] { t.TypeName, t.Bytes });

    [Theory]
    [MemberData(nameof(AllTargets))]
    public void Roundtrip_CSharp_To_Python_To_CSharp(string typeName, byte[] canonicalBytes)
    {
        var inFile = Path.GetTempFileName();
        var outFile = inFile + ".out";

        try
        {
            File.WriteAllBytes(inFile, canonicalBytes);

            var repoRoot = FindRepoRoot();

            var psi = new ProcessStartInfo
            {
                FileName = "uv",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add("contracts/roundtrip");
            psi.ArgumentList.Add("python");
            psi.ArgumentList.Add("contracts/roundtrip/harness.py");
            psi.ArgumentList.Add("--in");
            psi.ArgumentList.Add(inFile);
            psi.ArgumentList.Add("--type");
            psi.ArgumentList.Add(typeName);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(outFile);

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException(
                    "`uv` not found on PATH — install uv (https://docs.astral.sh/uv/) to run the CONT-04 harness.");

            if (!p.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                Assert.Fail($"harness.py timed out after 30s for type {typeName}");
            }

            var stderr = p.StandardError.ReadToEnd();
            var stdout = p.StandardOutput.ReadToEnd();
            Assert.True(
                p.ExitCode == 0,
                $"harness.py exited {p.ExitCode} for type {typeName}.\nstdout:\n{stdout}\nstderr:\n{stderr}");

            var roundtripped = File.ReadAllBytes(outFile);
            Assert.Equal(canonicalBytes, roundtripped);
        }
        finally
        {
            TryDelete(inFile);
            TryDelete(outFile);
        }
    }

    /// <summary>
    /// Walk up from the test assembly's base directory until we find the
    /// Bifrost.sln anchor. Used as the subprocess working directory so the
    /// relative path <c>contracts/roundtrip/harness.py</c> resolves regardless
    /// of test-host layout (local <c>dotnet test</c>, CI, Rider runner, etc.).
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Bifrost.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Bifrost.sln not found walking up from {AppContext.BaseDirectory}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort tempfile cleanup
        }
    }
}
