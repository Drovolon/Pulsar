using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Pulsar.Broadcast.Prepare;

public class CacheManager
{
    // included in all cache keys. change to force global cache invalidation
    private const string PipelineVersion = "v1";
    
    /// <summary>Max size of the cache on disk</summary>
    public long CacheCapBytes { get; init; } = 1L << 26; // 64 MiB
    
    private readonly string cacheDirectory;

    public CacheManager(string cacheDirectory)
    {
        this.cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(cacheDirectory);
    }

    public string CachePathFor(string path)
    {
        return Path.Combine(cacheDirectory, KeyFor(path));
    }

    public void TryEvictLru(string filePathsToSkip)
    {
        try
        {
            var files = new DirectoryInfo(cacheDirectory)
                        .GetFiles("*.opus")
                        .OrderBy(f => f.LastAccessTimeUtc).ToArray();
            var currentSize = files.Sum(f => f.Length);
            
            if (currentSize <= CacheCapBytes) return;
            
            foreach (var f in files)
            {
                if (f.FullName == filePathsToSkip) continue;
                try
                {
                    var freed = f.Length;
                    f.Delete();
                    currentSize -= freed;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Failed to delete cache file {file}", f.FullName);
                }

                if (currentSize <= CacheCapBytes) return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "SyncPrep LRU eviction failed");
        }
    }
    
    private static string KeyFor(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("Cache file not found", path);
        var raw = $"{PipelineVersion}|{path}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32];
    }
}
