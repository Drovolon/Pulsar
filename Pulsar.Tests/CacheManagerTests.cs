using System;
using System.IO;
using Pulsar.Broadcast.Prepare;
using Xunit;

namespace Pulsar.Tests;

public class CacheManagerTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-cm-test-");

    public void Dispose() => dir.Delete(recursive: true);

    private string CreateArtifact(string name, int bytes, TimeSpan age)
    {
        // Cache artifacts are extension-less content hashes (CachePathFor), so use
        // realistic hash-like names.
        var path = Path.Combine(dir.FullName, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void Evicts_oldest_artifacts_when_over_the_cap()
    {
        var cm = new CacheManager(dir.FullName) { CacheCapBytes = 1000 };
        var oldest = CreateArtifact("AAAA0000", 600, TimeSpan.FromHours(2));
        var middle = CreateArtifact("BBBB1111", 600, TimeSpan.FromHours(1));
        var newest = CreateArtifact("CCCC2222", 600, TimeSpan.Zero);

        cm.TryEvictLru(newest);

        Assert.False(File.Exists(oldest), "oldest artifact evicted");
        Assert.False(File.Exists(middle), "second-oldest evicted to get under the cap");
        Assert.True(File.Exists(newest), "just-produced artifact survives");
    }

    [Fact]
    public void Pinned_artifacts_survive_eviction()
    {
        var cm = new CacheManager(dir.FullName) { CacheCapBytes = 1000 };
        var live = CreateArtifact("AAAA0000", 600, TimeSpan.FromHours(2)); // oldest, but on the air
        var other = CreateArtifact("BBBB1111", 600, TimeSpan.FromHours(1));
        var newest = CreateArtifact("CCCC2222", 600, TimeSpan.Zero);

        cm.TryEvictLru(newest, live);

        Assert.True(File.Exists(live), "the artifact the live manifest points at is never deleted");
        Assert.False(File.Exists(other));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void InFlight_transcode_temp_files_are_not_touched()
    {
        var cm = new CacheManager(dir.FullName) { CacheCapBytes = 100 };
        var tmp = CreateArtifact("DDDD3333.tmp", 600, TimeSpan.FromHours(3));

        cm.TryEvictLru();

        Assert.True(File.Exists(tmp), "a transcode's .tmp working file is not eviction's to delete");
    }
}
