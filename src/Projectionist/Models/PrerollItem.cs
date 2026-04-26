using System;

namespace Jellyfin.Plugin.Projectionist.Models;

/// <summary>
/// Lightweight representation of a discovered preroll file.
/// </summary>
public sealed class PrerollItem
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public long FileSizeBytes { get; init; }
    public DateTime LastModifiedUtc { get; init; }
    public Guid DeterministicId { get; init; }
}
