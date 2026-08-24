using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Beefweb.Client;

namespace Pulsar.Tests.Fakes;

/// <summary>
/// In-process beefweb server: a real Beefweb.Client PlayerClient talks to this through
/// its normal HTTP/JSON/SSE wire formats, just without sockets. Tests script the DJ's
/// player state here; for SSE, each state push becomes an update frame.
/// </summary>
public sealed class FakeBeefwebServer : HttpMessageHandler
{
    private readonly Lock @lock = new();
    private readonly List<SsePushStream> sessions = [];
    private readonly List<string> posts = [];

    // The DJ's player, as beefweb would report it over the wire.
    private string? path;
    private string artist = "";
    private string title = "";
    private string playbackState = "stopped";
    private double positionSeconds;
    private double durationSeconds = 60;
    private object[] options = [];
    private string? playlistId;
    private int playlistItemIndex = -1;
    private readonly Dictionary<int, string> playlistItems = [];
    private readonly List<string> playQueue = [];

    private int sessionsOpened;
    private int requestsReceived;

    /// <summary>When true, every request fails like a dead/unreachable server.</summary>
    public volatile bool Down;

    /// <summary>Optional response gate for a POST route, used to prove callers serialize commands.</summary>
    public Func<string, Task?>? StallPost { get; set; }

    /// <summary>POST paths received, in arrival order (e.g. "/api/player/stop").</summary>
    public string[] Posts
    {
        get
        {
            lock (@lock)
            {
                return [.. posts];
            }
        }
    }

    /// <summary>Total SSE subscriptions ever opened; grows by one per (re)connect.</summary>
    public int SseSessionsOpened => Volatile.Read(ref sessionsOpened);

    /// <summary>Total HTTP requests attempted, including requests rejected while Down.</summary>
    public int RequestsReceived => Volatile.Read(ref requestsReceived);

    /// <summary>A real PlayerClient wired to this server.</summary>
    public PlayerClient CreateClient() => new(new HttpClient(this, false), new Uri("http://fake-beefweb/"));

    public void SetPlaying(
        string trackPath, double positionSeconds = 5, double durationSeconds = 60, string artist = "", string title = "")
    {
        lock (@lock)
        {
            path = trackPath;
            this.positionSeconds = positionSeconds;
            this.durationSeconds = durationSeconds;
            this.artist = artist;
            this.title = title;
            playbackState = "playing";
        }
    }

    public void SetPaused()
    {
        lock (@lock)
        {
            playbackState = "paused";
        }
    }

    public void SetStopped()
    {
        lock (@lock)
        {
            path = null;
            playbackState = "stopped";
        }
    }

    /// <summary>Player options as beefweb reports them (id + enum names + current index).</summary>
    public void SetOptions(params (string Id, string[] EnumNames, int Value)[] opts)
    {
        lock (@lock)
        {
            options =
            [
                .. Array.ConvertAll(opts, o => (object)new
                {
                    id = o.Id,
                    name = o.Id,
                    type = "enum",
                    value = o.Value,
                    enumNames = o.EnumNames,
                }),
            ];
        }
    }

    /// <summary>The active playlist: which item is playing and what paths sit at which indices.</summary>
    public void SetActivePlaylist(string id, int activeIndex, params (int Index, string Path)[] items)
    {
        lock (@lock)
        {
            playlistId = id;
            playlistItemIndex = activeIndex;
            playlistItems.Clear();
            foreach (var (i, p) in items) playlistItems[i] = p;
        }
    }

    public void SetPlayQueue(params string[] paths)
    {
        lock (@lock)
        {
            playQueue.Clear();
            playQueue.AddRange(paths);
        }
    }

    /// <summary>Sends the current player state to every live SSE subscriber.</summary>
    public void PushUpdate()
    {
        var frame = Frame();
        SsePushStream[] targets;
        lock (@lock)
        {
            targets = [.. sessions];
        }

        foreach (var s in targets) s.Push(frame);
    }

    /// <summary>Kills all live SSE streams: cleanly (server closed) or with a transport error.</summary>
    public void DropSseStreams(Exception? error = null)
    {
        SsePushStream[] targets;
        lock (@lock)
        {
            targets = [.. sessions];
            sessions.Clear();
        }

        foreach (var s in targets) s.Complete(error);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref requestsReceived);
        if (Down) throw new HttpRequestException("fake beefweb is down");

        var route = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post)
        {
            lock (@lock)
            {
                posts.Add(route);
            }

            if (StallPost?.Invoke(route) is { } hang) await hang.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return route switch
        {
            "/api/player" => Json(new { player = PlayerJson() }),
            "/api/playqueue" => Json(new { playQueue = QueueJson() }),
            "/api/query/updates" => OpenSse(),
            _ when route.StartsWith("/api/playlists/") && route.Contains("/items/") => Json(
                new { playlistItems = PlaylistItemsJson(route) }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private object PlayerJson()
    {
        lock (@lock)
        {
            return new
            {
                activeItem = new
                {
                    playlistId,
                    playlistIndex = playlistId is null ? -1 : 0,
                    index = playlistItemIndex,
                    position = positionSeconds,
                    duration = durationSeconds,
                    // Column order must match what Pulsar requests: %path%, %artist%, %title%.
                    columns = new[] { path ?? "", artist, title },
                },
                playbackState,
                options,
            };
        }
    }

    private object[] QueueJson()
    {
        lock (@lock)
        {
            return
            [
                .. playQueue.ConvertAll(p => (object)new
                {
                    playlistId = playlistId ?? "q",
                    playlistIndex = 0,
                    itemIndex = 0,
                    columns = new[] { p },
                }),
            ];
        }
    }

    // Route shape: /api/playlists/{id}/items/{offset}:{count}
    private object PlaylistItemsJson(string route)
    {
        var rangePart = route[(route.LastIndexOf('/') + 1)..];
        var offset = int.Parse(rangePart.Split(':')[0]);
        lock (@lock)
        {
            var items = playlistItems.TryGetValue(offset, out var p) ? new[] { new { columns = new[] { p } } } : [];
            return new { offset, totalCount = playlistItems.Count, items };
        }
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private HttpResponseMessage OpenSse()
    {
        var stream = new SsePushStream();
        lock (@lock)
        {
            sessions.Add(stream);
        }

        Interlocked.Increment(ref sessionsOpened);
        stream.Push(Frame()); // beefweb sends the current state upon subscribing

        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private byte[] Frame() =>
        Encoding.UTF8.GetBytes("data:" + JsonSerializer.Serialize(new { player = PlayerJson() }) + "\n\n");

    /// <summary>
    /// Readable stream fed by Push(); read blocks until data arrives. Complete() ends it
    /// (optionally with a transport error), which is how an SSE connection drop looks.
    /// </summary>
    private sealed class SsePushStream : Stream
    {
        private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>();
        private ReadOnlyMemory<byte> current;

        public void Push(byte[] frame) => chunks.Writer.TryWrite(frame);
        public void Complete(Exception? error) => chunks.Writer.TryComplete(error);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (current.IsEmpty)
            {
                if (!await chunks.Reader.WaitToReadAsync(ct)) return 0;
                if (chunks.Reader.TryRead(out var next)) current = next;
            }

            var n = Math.Min(buffer.Length, current.Length);
            current.Span[..n].CopyTo(buffer.Span);
            current = current[n..];
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
