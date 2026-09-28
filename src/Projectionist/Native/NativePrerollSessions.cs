using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Projectionist.Native;

/// <summary>
/// A native app's episode playback that has prerolls queued for it.
/// </summary>
public sealed class NativePrerollSession
{
    private readonly TaskCompletionSource<long> _spliced = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NativePrerollSession(string playSessionId, string deviceId, Guid episodeId, IReadOnlyList<string> prerollPaths)
    {
        PlaySessionId = playSessionId;
        DeviceId = deviceId;
        EpisodeId = episodeId;
        PrerollPaths = prerollPaths;
    }

    public string PlaySessionId { get; }

    public string DeviceId { get; }

    public Guid EpisodeId { get; }

    public IReadOnlyList<string> PrerollPaths { get; }

    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    /// <summary>Serialises the first playlist request, which does the conversion.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>The converted prerolls once the first playlist was built; reused on refetch.</summary>
    public IReadOnlyList<PrerollHlsEntry>? Entries { get; set; }

    /// <summary>
    /// Ticks of preroll actually put in front of the episode; 0 if the
    /// stream went out without one (conversion timed out, unsupported
    /// format). Everything that shifts times waits on this, so a stream
    /// that played plain is never shifted.
    /// </summary>
    public Task<long> SplicedTicks => _spliced.Task;

    public bool IsDecided => _spliced.Task.IsCompleted;

    public void Decide(long ticks) => _spliced.TrySetResult(Math.Max(0, ticks));

    /// <summary>The offset if already known, otherwise waits up to <paramref name="wait"/>.</summary>
    public async Task<long> GetOffsetAsync(TimeSpan wait)
    {
        var done = await Task.WhenAny(_spliced.Task, Task.Delay(wait)).ConfigureAwait(false);
        return done == _spliced.Task ? _spliced.Task.Result : 0;
    }
}

/// <summary>In-memory registry of <see cref="NativePrerollSession"/>s.</summary>
public sealed class NativePrerollSessions
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(8);
    private readonly ConcurrentDictionary<string, NativePrerollSession> _byPlaySession = new(StringComparer.OrdinalIgnoreCase);

    // Sessions whose stream really has a preroll in front and that haven't
    // stopped yet. Progress reports from every client on the server pass
    // through the middleware, so they are only read while this is non-empty.
    private readonly ConcurrentDictionary<string, long> _playingWithOffset = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => _byPlaySession.IsEmpty;

    public bool AnyPlayingWithOffset => !_playingWithOffset.IsEmpty;

    public void MarkPlaying(string playSessionId, long offsetTicks)
    {
        if (offsetTicks > 0) _playingWithOffset[playSessionId] = offsetTicks;
    }

    public long? OffsetForReport(string playSessionId)
        => _playingWithOffset.TryGetValue(playSessionId, out var t) ? t : null;

    public void MarkStopped(string playSessionId) => _playingWithOffset.TryRemove(playSessionId, out _);

    public void Add(NativePrerollSession session)
    {
        _byPlaySession[session.PlaySessionId] = session;
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var kv in _byPlaySession)
        {
            if (kv.Value.CreatedUtc < cutoff)
            {
                _byPlaySession.TryRemove(kv.Key, out _);
                _playingWithOffset.TryRemove(kv.Key, out _);
            }
        }
    }

    public NativePrerollSession? Get(string? playSessionId)
        => !string.IsNullOrEmpty(playSessionId) && _byPlaySession.TryGetValue(playSessionId, out var s) ? s : null;

    /// <summary>Latest session for this device and episode (media-segment lookups carry no session id).</summary>
    public NativePrerollSession? LatestFor(string? deviceId, Guid episodeId)
        => string.IsNullOrEmpty(deviceId)
            ? null
            : _byPlaySession.Values
                .Where(s => s.EpisodeId == episodeId && string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal))
                .OrderByDescending(s => s.CreatedUtc)
                .FirstOrDefault();
}
