using System.IO;
using Pulsar.Broadcast;
using Xunit;

namespace Pulsar.Tests;

/// <summary>ResolveUnder resolves configured mod paths that exist on disk.</summary>
public class PenumbraPathTests : System.IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("pulsar-penumbra-test-");
    public void Dispose() => root.Delete(true);

    public PenumbraPathTests()
    {
        Directory.CreateDirectory(Path.Combine(root.FullName, "CoolMod"));
    }

    [Fact]
    public void Resolves_an_existing_mod() =>
        Assert.Equal(Path.Combine(root.FullName, "CoolMod"), PenumbraIntegration.ResolveUnder(root.FullName, "CoolMod"));

    [Fact]
    public void Parent_segments_are_canonicalized() =>
        Assert.Equal(Path.Combine(root.FullName, "CoolMod"),
                     PenumbraIntegration.ResolveUnder(root.FullName, "nested/../CoolMod"));

    [Fact]
    public void A_nonexistent_mod_resolves_to_null() =>
        Assert.Null(PenumbraIntegration.ResolveUnder(root.FullName, "NoSuchMod"));
}
