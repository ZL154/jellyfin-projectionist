using System;
using System.Collections.Generic;

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
    public List<string> Tags { get; init; } = new();
    public double Weight { get; init; } = 1.0;
    public string? Rating { get; init; }
    public ScheduleRule? Schedule { get; init; }
    /// <summary>Folder this preroll came from (so per-folder default tags can be applied).</summary>
    public string SourceFolder { get; init; } = string.Empty;
}
