using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public void Pick_NoSalt_MatchesPreviousAlgorithm()
    {
        var ids = new[] { "a", "b", "c", "d", "e", "f", "g" };
        var lists = ids.Select(id => List(id)).ToArray();
        for (var i = 0; i < 20; i++)
        {
            var day = Day.AddDays(i);
            var expected = PreviousAlgorithm(UserA, day, ids, 3);

            Assert.Equal(expected, DiscoveryPicker.Pick(UserA, day, lists, 3, null).Select(l => l.Id));
            Assert.Equal(expected, DiscoveryPicker.Pick(UserA, day, lists, 3, string.Empty).Select(l => l.Id));
            Assert.Equal(expected, DiscoveryPicker.Pick(UserA, day, ids, 3));
        }
    }

    [Fact]
    public void Pick_Salt_ChangesOrderAndIsStable()
    {
        var lists = Lists.Select(id => List(id)).ToArray();

        var salted = DiscoveryPicker.Pick(UserA, Day, lists, 5, "salt-1").Select(l => l.Id).ToArray();

        Assert.Equal(salted, DiscoveryPicker.Pick(UserA, Day, lists, 5, "salt-1").Select(l => l.Id));
        Assert.Equal(SaltedOrder(UserA, Day, Lists, "salt-1"), salted);
        var differs = Enumerable.Range(0, 20).Any(i =>
            !DiscoveryPicker.Pick(UserA, Day, lists, 5, "salt-" + i).Select(l => l.Id).SequenceEqual(PreviousAlgorithm(UserA, Day, Lists, 5)));
        Assert.True(differs);
    }

    [Fact]
    public void Pick_TakesOneListPerGroup()
    {
        var lists = new[]
        {
            List("a1", "genre-a"), List("a2", "genre-a"), List("a3", "genre-a"),
            List("b1", "genre-b"), List("b2", "genre-b"),
            List("solo"),
        };
        var order = PreviousAlgorithm(UserA, Day, lists.Select(l => l.Id), 10);
        var expected = new List<string>();
        var groups = new HashSet<string>();
        foreach (var id in order)
        {
            var group = lists.Single(l => l.Id == id).Group;
            if (group is null || groups.Add(group))
            {
                expected.Add(id);
            }
        }

        var picked = DiscoveryPicker.Pick(UserA, Day, lists, 10, null);

        Assert.Equal(expected, picked.Select(l => l.Id));
        Assert.Equal(3, picked.Count);
        Assert.Single(picked, l => l.Group == "genre-a");
        Assert.Single(picked, l => l.Group == "genre-b");
        Assert.Single(picked, l => l.Group is null);
    }

    [Fact]
    public void Pick_NullGroupListsAreIndependent()
    {
        var lists = new[] { List("n1"), List("n2"), List("n3", "  "), List("g1", "g"), List("g2", "g") };

        var picked = DiscoveryPicker.Pick(UserA, Day, lists, 10, null);

        Assert.Equal(4, picked.Count);
        Assert.Equal(new[] { "n1", "n2", "n3" }, picked.Select(l => l.Id).Where(id => id.StartsWith('n')).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Pick_FewerGroupsThanMaxRows_Stops()
    {
        var lists = new[]
        {
            List("a1", "a"), List("a2", "a"), List("a3", "a"),
            List("b1", "b"), List("b2", "b"), List("b3", "b"),
        };

        for (var i = 0; i < 10; i++)
        {
            var picked = DiscoveryPicker.Pick(UserA, Day.AddDays(i), lists, 5, null);

            Assert.Equal(2, picked.Count);
            Assert.Equal(new[] { "a", "b" }, picked.Select(l => l.Group!).Order(StringComparer.Ordinal));
        }
    }

    private static DiscoveryList List(string id, string? group = null)
        => new(id, "Title of " + id, "tests", [new DiscoveryFilmRef(1, null)], group);

    // The picker as it was before groups and salts: SHA-256 of "{userId:N}|{day:yyyy-MM-dd}|{listId}".
    private static string[] PreviousAlgorithm(Guid userId, DateOnly day, IEnumerable<string> ids, int maxRows)
        => ids.Distinct(StringComparer.Ordinal)
            .Select(id => (Id: id, Hash: Sha(string.Create(CultureInfo.InvariantCulture, $"{userId:N}|{day:yyyy-MM-dd}|{id}"))))
            .OrderBy(x => x.Hash, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Take(maxRows)
            .Select(x => x.Id)
            .ToArray();

    private static string[] SaltedOrder(Guid userId, DateOnly day, IEnumerable<string> ids, string salt)
        => ids.Select(id => (Id: id, Hash: Sha(string.Create(CultureInfo.InvariantCulture, $"{userId:N}|{day:yyyy-MM-dd}|{id}|{salt}"))))
            .OrderBy(x => x.Hash, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.Id)
            .ToArray();

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
