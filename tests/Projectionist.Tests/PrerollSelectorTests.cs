using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Models;
using Jellyfin.Plugin.Projectionist.Services;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class PrerollSelectorTests
{
    private static List<PrerollItem> Pool(int n)
    {
        var list = new List<PrerollItem>();
        for (var i = 0; i < n; i++)
        {
            list.Add(new PrerollItem
            {
                Path = $"/p/{i}.mp4",
                FileName = $"{i:D2}.mp4",
                FileSizeBytes = 1024,
                LastModifiedUtc = DateTime.UtcNow.AddDays(-i),
                DeterministicId = Guid.NewGuid(),
                Tags = new List<string> { "default" },
                Weight = 1.0,
            });
        }
        return list;
    }

    [Fact]
    public void Select_EmptyPool_ReturnsEmpty()
    {
        var sel = new PrerollSelector();
        Assert.Empty(sel.Select(new List<PrerollItem>(), 5, SelectionMode.Random));
    }

    [Fact]
    public void Select_ZeroCount_ReturnsEmpty()
    {
        var sel = new PrerollSelector();
        Assert.Empty(sel.Select(Pool(10), 0, SelectionMode.Random));
    }

    [Fact]
    public void Select_CountGreaterThanPool_CappedAtPoolSize()
    {
        var sel = new PrerollSelector();
        var picks = sel.Select(Pool(3), 10, SelectionMode.Random);
        Assert.Equal(3, picks.Count);
    }

    [Fact]
    public void Select_Random_ReturnsNDistinctPicks()
    {
        var sel = new PrerollSelector();
        var picks = sel.Select(Pool(20), 5, SelectionMode.Random);
        Assert.Equal(5, picks.Count);
        Assert.Equal(5, picks.Select(p => p.DeterministicId).Distinct().Count());
    }

    [Fact]
    public void Select_Sequential_ReturnsCount()
    {
        var sel = new PrerollSelector();
        var picks = sel.Select(Pool(4), 2, SelectionMode.Sequential);
        Assert.Equal(2, picks.Count);
    }

    [Fact]
    public void Select_Weighted_ReturnsCount()
    {
        var sel = new PrerollSelector();
        var picks = sel.Select(Pool(10), 3, SelectionMode.Weighted);
        Assert.Equal(3, picks.Count);
    }

    [Fact]
    public void Select_EqualRotation_ReturnsCount()
    {
        var sel = new PrerollSelector();
        var picks = sel.Select(Pool(5), 3, SelectionMode.EqualRotation);
        Assert.Equal(3, picks.Count);
    }

    [Fact]
    public void Select_RecencyBoost_ReturnsCount()
    {
        var sel = new PrerollSelector();
        var picks = sel.Select(Pool(8), 2, SelectionMode.RecencyBoost);
        Assert.Equal(2, picks.Count);
    }
}
