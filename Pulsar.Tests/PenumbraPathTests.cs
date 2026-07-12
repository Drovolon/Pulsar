using System.IO;
using Pulsar.Broadcast;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// ResolveUnder is PenumbraIntegration's path sanitizer: a mod directory NAME
/// (from config) must never resolve outside the Penumbra mod root.
/// </summary>
public class PenumbraPathTests : System.IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("pulsar-penumbra-test-");
    public void Dispose() => root.Delete(recursive: true);

    public PenumbraPathTests() => Directory.CreateDirectory(Path.Combine(root.FullName, "CoolMod"));

    [Fact]
    public void Resolves_an_existing_mod()
        => Assert.Equal(Path.Combine(root.FullName, "CoolMod"),
                        PenumbraIntegration.ResolveUnder(root.FullName, "CoolMod"));

    [Fact]
    public void Dot_dot_cannot_escape_the_mod_root()
        => Assert.Null(PenumbraIntegration.ResolveUnder(root.FullName, ".."));

    [Fact]
    public void A_traversal_path_reduces_to_its_leaf_or_dies()
    {
        // The leaf of "CoolMod/../.." is ".." - refused.
        Assert.Null(PenumbraIntegration.ResolveUnder(root.FullName, "CoolMod/../.."));
        // "nested/CoolMod" -> leaf "CoolMod" -> resolves (mod directory names are leaves by contract).
        Assert.Equal(Path.Combine(root.FullName, "CoolMod"),
                     PenumbraIntegration.ResolveUnder(root.FullName, "nested/CoolMod"));
    }

    [Fact]
    public void Dot_and_empty_are_refused()
    {
        Assert.Null(PenumbraIntegration.ResolveUnder(root.FullName, "."));
        Assert.Null(PenumbraIntegration.ResolveUnder(root.FullName, "CoolMod/")); // leaf is ""
    }

    [Fact]
    public void A_nonexistent_mod_resolves_to_null()
        => Assert.Null(PenumbraIntegration.ResolveUnder(root.FullName, "NoSuchMod"));
}
