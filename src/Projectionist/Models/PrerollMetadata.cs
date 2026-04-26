using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Projectionist.Models;

/// <summary>
/// Optional sidecar metadata for a preroll file. Loaded either from a per-file
/// `<filename>.json` sidecar or a `prerolls.json` mapping in the folder root.
/// </summary>
public sealed class PrerollMetadata
{
    /// <summary>Tags this preroll carries (e.g. "default", "halloween", "nsfw").</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Selection weight (1.0 = baseline). Higher = chosen more often.</summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>Maturity rating like "G", "PG", "PG-13", "R". null = no constraint.</summary>
    public string? Rating { get; set; }

    /// <summary>Optional schedule constraint — only eligible when current date matches.</summary>
    public ScheduleRule? Schedule { get; set; }
}

public sealed class ScheduleRule
{
    /// <summary>Months of the year (1-12) when this preroll is eligible. Empty = any.</summary>
    public List<int> Months { get; set; } = new();

    /// <summary>Days of the week (0=Sunday..6=Saturday) when eligible. Empty = any.</summary>
    public List<int> DaysOfWeek { get; set; } = new();

    /// <summary>Inclusive start hour-of-day (0-23). null = no lower bound.</summary>
    public int? StartHour { get; set; }

    /// <summary>Exclusive end hour-of-day (0-24). null = no upper bound.</summary>
    public int? EndHour { get; set; }

    /// <summary>Specific date range like "12-20..12-31" (MM-DD..MM-DD). null = none.</summary>
    public string? DateRange { get; set; }

    public bool Matches(DateTime nowLocal)
    {
        if (Months.Count > 0 && !Months.Contains(nowLocal.Month)) return false;
        if (DaysOfWeek.Count > 0 && !DaysOfWeek.Contains((int)nowLocal.DayOfWeek)) return false;
        if (StartHour.HasValue && nowLocal.Hour < StartHour.Value) return false;
        if (EndHour.HasValue && nowLocal.Hour >= EndHour.Value) return false;
        if (!string.IsNullOrWhiteSpace(DateRange))
        {
            var parts = DateRange.Split("..");
            if (parts.Length == 2 &&
                TryParseMmDd(parts[0], out var startMonth, out var startDay) &&
                TryParseMmDd(parts[1], out var endMonth, out var endDay))
            {
                var monthDay = nowLocal.Month * 100 + nowLocal.Day;
                var startMd = startMonth * 100 + startDay;
                var endMd = endMonth * 100 + endDay;
                if (startMd <= endMd)
                {
                    if (monthDay < startMd || monthDay > endMd) return false;
                }
                else
                {
                    // wraps year boundary (e.g. Dec 20 .. Jan 5)
                    if (monthDay < startMd && monthDay > endMd) return false;
                }
            }
        }
        return true;
    }

    private static bool TryParseMmDd(string s, out int month, out int day)
    {
        month = day = 0;
        var parts = s.Trim().Split('-');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out month) && int.TryParse(parts[1], out day);
    }
}
