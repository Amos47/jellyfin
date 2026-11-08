using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Discovery;
using Xunit;

namespace Jellyfin.Controller.Tests.Discovery;

public class DiscoveryPickerTests
{
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly Day = new(2026, 11, 7);
    private static readonly string[] Lists = { "a", "b", "c", "d", "e" };

    [Fact]
    public void SameUserSameDay_SameResult()
    {
        Assert.Equal(DiscoveryPicker.Pick(UserA, Day, Lists, 2), DiscoveryPicker.Pick(UserA, Day, Lists, 2));
    }

    [Fact]
    public void DifferentDay_UsuallyDifferent()
    {
        var results = Enumerable.Range(0, 30)
            .Select(i => string.Join(",", DiscoveryPicker.Pick(UserA, Day.AddDays(i), Lists, 2)))
            .Distinct()
            .Count();

        Assert.True(results >= 10, $"only {results} distinct results");
    }

    [Fact]
    public void FewerListsThanMax_ReturnsAll()
    {
        var result = DiscoveryPicker.Pick(UserA, Day, new[] { "a", "b" }, 5);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "a", "b" }, result.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void MaxRowsZeroOrNegative_Empty(int maxRows)
    {
        Assert.Empty(DiscoveryPicker.Pick(UserA, Day, Lists, maxRows));
    }

    [Fact]
    public void IndependentOfInputOrder()
    {
        Assert.Equal(
            DiscoveryPicker.Pick(UserA, Day, Lists, 3),
            DiscoveryPicker.Pick(UserA, Day, Lists.Reverse(), 3));
    }

    [Fact]
    public void DifferentUsers_CanDiffer()
    {
        var differs = Enumerable.Range(0, 30).Any(i =>
            !DiscoveryPicker.Pick(UserA, Day.AddDays(i), Lists, 2)
                .SequenceEqual(DiscoveryPicker.Pick(UserB, Day.AddDays(i), Lists, 2)));

        Assert.True(differs);
    }

    [Fact]
    public void DuplicateIds_AppearOnce()
    {
        var result = DiscoveryPicker.Pick(UserA, Day, new[] { "a", "a", "b" }, 5);

        Assert.Equal(2, result.Count);
    }
}
