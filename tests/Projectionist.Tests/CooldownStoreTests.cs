using System;
using Jellyfin.Plugin.Projectionist.Services;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class CooldownStoreTests
{
    [Fact]
    public void IsCooling_ReturnsFalse_WhenCooldownHoursIsZero()
    {
        var store = new CooldownStore();
        var feature = Guid.NewGuid();
        var preroll = Guid.NewGuid();
        store.Record(feature, preroll);
        Assert.False(store.IsCooling(feature, preroll, 0));
    }

    [Fact]
    public void IsCooling_ReturnsFalse_WhenNothingRecorded()
    {
        var store = new CooldownStore();
        Assert.False(store.IsCooling(Guid.NewGuid(), Guid.NewGuid(), 24));
    }

    [Fact]
    public void IsCooling_ReturnsTrue_AfterRecord()
    {
        var store = new CooldownStore();
        var feature = Guid.NewGuid();
        var preroll = Guid.NewGuid();
        store.Record(feature, preroll);
        Assert.True(store.IsCooling(feature, preroll, 24));
    }

    [Fact]
    public void Record_IsScopedPerPair()
    {
        var store = new CooldownStore();
        var feature1 = Guid.NewGuid();
        var feature2 = Guid.NewGuid();
        var preroll = Guid.NewGuid();
        store.Record(feature1, preroll);
        Assert.True(store.IsCooling(feature1, preroll, 24));
        Assert.False(store.IsCooling(feature2, preroll, 24));
    }

    [Fact]
    public void Record_OverwritesTimestamp()
    {
        var store = new CooldownStore();
        var feature = Guid.NewGuid();
        var preroll = Guid.NewGuid();
        store.Record(feature, preroll);
        store.Record(feature, preroll);
        Assert.True(store.IsCooling(feature, preroll, 24));
    }
}
