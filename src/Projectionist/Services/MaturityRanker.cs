using System;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Maps maturity rating strings to a 0-100 score. Supports MPAA, BBFC,
/// German FSK, French CSA, Australian ACB, Danish, Spanish ICAA, Japanese Eirin.
/// FEATURES score 100 if unknown - don't gate on unclassifiable feature.
/// PREROLLS score 0 if unknown - untagged prerolls always pass.
/// </summary>
public static class MaturityRanker
{
    public static int Score(string? rating) => ScoreInternal(rating, unknown: 100);
    public static int ScorePreroll(string? rating) => ScoreInternal(rating, unknown: 0);

    private static int ScoreInternal(string? rating, int unknown)
    {
        if (string.IsNullOrWhiteSpace(rating)) return unknown;
        var r = rating.Trim().ToUpperInvariant().Replace(" ", string.Empty);

        // US MPAA + TV
        if (r.Contains("NC-17") || r is "X" or "AO") return 100;
        if (r is "R" or "M" or "MA" or "TV-MA") return 80;
        if (r is "PG-13" or "TV-14") return 60;
        if (r is "PG" or "TV-PG") return 40;
        if (r is "G" or "TV-G" or "TV-Y" or "TV-Y7") return 20;

        // UK BBFC
        if (r is "U" or "UC") return 20;
        if (r is "12" or "12A") return 60;
        if (r is "15") return 80;
        if (r is "18") return 100;

        // German FSK
        if (r is "FSK0") return 20;
        if (r is "FSK6") return 30;
        if (r is "FSK12") return 60;
        if (r is "FSK16") return 80;
        if (r is "FSK18") return 100;

        // French CSA
        if (r is "TP") return 20;
        if (r is "-10") return 40;
        if (r is "-12") return 60;
        if (r is "-16") return 80;
        if (r is "-18") return 100;

        // Australian ACB
        if (r is "MA15+" or "MA15") return 80;
        if (r is "R18+" or "R18") return 100;

        // Danish
        if (r is "A" or "TILLADTFORALLE") return 20;
        if (r is "7") return 30;
        if (r is "11") return 50;

        // Spanish ICAA
        if (r is "APTA") return 20;
        if (r is "16") return 80;

        // Japanese Eirin
        if (r is "PG12") return 40;
        if (r is "R15+" or "R15") return 80;

        return 50;
    }
}
