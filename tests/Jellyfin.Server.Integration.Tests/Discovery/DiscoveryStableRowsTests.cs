using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Models.HomeRowDtos;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.HomeRows;
using Jellyfin.HomeRows.Configuration;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

/// <summary>
/// A derived Movie discovery row id names its list, not its position: a client that keeps a row's title from the
/// first load and refetches its items by id later keeps getting that list's films, whatever the picks do meanwhile.
/// Its own host: a re-pick changes the day's picks for every user.
/// </summary>
public sealed class DiscoveryStableRowsTests : IClassFixture<JellyfinApplicationFactory>
{
    private const int MaxRows = 3;

    private readonly JellyfinApplicationFactory _factory;

    public DiscoveryStableRowsTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private IDiscoveryStore Store => _factory.Services.GetRequiredService<IDiscoveryStore>();

    private IUserManager UserManager => _factory.Services.GetRequiredService<IUserManager>();

    private IHomeRowConfigurationService Layouts => _factory.Services.GetRequiredService<IHomeRowConfigurationService>();

    [Fact]
    public async Task RePick_BetweenLayoutAndItems_KeepsTitleAndFilmsTogether()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        var listIds = await PutListsAsync(client, "sr-repick", 8, 2100);
        try
        {
            await SaveLayoutAsync(admin);
            var rowIds = await GetDiscoveryRowIdsAsync(client);
            Assert.Equal(MaxRows, rowIds.Length);
            var before = await SnapshotAsync(client, rowIds);
            await AssertCoherentAsync(before);

            // Re-pick until today's lists change: the layout an open app holds is now stale.
            var today = DateOnly.FromDateTime(DateTime.Now);
            var titlesBefore = before.Select(r => r.Title).Order(StringComparer.Ordinal).ToArray();
            var changed = false;
            for (var attempt = 0; attempt < 10 && !changed; attempt++)
            {
                var salt = await ReshuffleAsync(client);
                var picked = DiscoveryPicker.Pick(admin.Id, today, await Store.GetListsAsync(Ct), MaxRows, salt).Select(l => l.Title);
                changed = !picked.Order(StringComparer.Ordinal).SequenceEqual(titlesBefore);
            }

            Assert.True(changed, "ten re-picks never changed the rows");

            // The stale ids still serve the same list: the same title with the same films.
            var after = await SnapshotAsync(client, rowIds);
            Assert.Equal(before.Select(r => r.Title), after.Select(r => r.Title));
            for (var i = 0; i < before.Length; i++)
            {
                Assert.Equal(before[i].Films, after[i].Films);
            }
        }
        finally
        {
            await Layouts.ResetUserLayoutAsync(admin, CancellationToken.None);
            await DeleteListsAsync(client, listIds);
        }
    }

    [Fact]
    public async Task RePick_FreshExpandedLayout_ShowsNewPicks()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        var listIds = await PutListsAsync(client, "sr-fresh", 8, 2200);
        try
        {
            await SaveLayoutAsync(admin);
            var salt = await ReshuffleAsync(client);
            var today = DateOnly.FromDateTime(DateTime.Now);
            var expected = DiscoveryPicker.Pick(admin.Id, today, await Store.GetListsAsync(Ct), MaxRows, salt).ToArray();

            var rowIds = await GetDiscoveryRowIdsAsync(client);
            var rows = await SnapshotAsync(client, rowIds);

            Assert.Equal(expected.Select(l => l.Title), rows.Select(r => r.Title));
            Assert.Equal(expected.Select(l => FilmIds(l.Films.Select(f => f.TmdbId))), rows.Select(r => r.Films));
        }
        finally
        {
            await Layouts.ResetUserLayoutAsync(admin, CancellationToken.None);
            await DeleteListsAsync(client, listIds);
        }
    }

    [Fact]
    public async Task ListReplacedOrRemoved_MidDay_StaleIdsStayCoherent()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        var listIds = await PutListsAsync(client, "sr-change", MaxRows, 2300);
        try
        {
            // Only three lists, so all three are picked.
            await SaveLayoutAsync(admin);
            var rowIds = await GetDiscoveryRowIdsAsync(client);
            var before = await SnapshotAsync(client, rowIds);
            Assert.Equal(listIds.Order(StringComparer.Ordinal), before.Select(r => ListIdOf(r.Title)).Order(StringComparer.Ordinal));
            var replaced = Array.FindIndex(before, r => ListIdOf(r.Title) == listIds[0]);
            var removed = Array.FindIndex(before, r => ListIdOf(r.Title) == listIds[1]);
            var kept = Array.FindIndex(before, r => ListIdOf(r.Title) == listIds[2]);

            // A refresh replaces one list's films and drops another.
            await PutListAsync(client, listIds[0], 2390, 2391, 2392);
            await DeleteListsAsync(client, listIds[1]);

            var replacedNow = await GetItemsAsync(client, rowIds[replaced]);
            Assert.Equal(before[replaced].Title, replacedNow.Title);
            Assert.Equal(FilmIds([2390, 2391, 2392]), replacedNow.Items.Select(i => i.Id).ToArray());

            // The dropped list's id no longer names anything the server can serve.
            using (var gone = await client.GetAsync($"HomeRows/{rowIds[removed]:D}/Items", Ct))
            {
                Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            }

            var keptNow = await GetItemsAsync(client, rowIds[kept]);
            Assert.Equal(before[kept].Title, keptNow.Title);
            Assert.Equal(before[kept].Films, keptNow.Items.Select(i => i.Id).ToArray());

            // A fresh layout shows only what is left.
            var fresh = await SnapshotAsync(client, await GetDiscoveryRowIdsAsync(client));
            Assert.Equal(new[] { listIds[0], listIds[2] }, fresh.Select(r => ListIdOf(r.Title)).Order(StringComparer.Ordinal));
        }
        finally
        {
            await Layouts.ResetUserLayoutAsync(admin, CancellationToken.None);
            await DeleteListsAsync(client, listIds);
        }
    }

    [Fact]
    public async Task DayChange_BetweenLayoutAndItems_KeepsTitleAndFilmsTogether()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        var listIds = await PutListsAsync(client, "sr-day", 8, 2400);
        try
        {
            await SaveLayoutAsync(admin);
            var provider = (IExpandingHomeRowProvider)_factory.Services.GetRequiredService<IHomeRowProviderFactory>().Get(HomeRowProviderIds.MovieDiscovery)!;
            var config = new JsonObject { ["maxRows"] = MaxRows };
            var now = DateTime.UtcNow;
            var rowIds = await GetDiscoveryRowIdsAsync(client);
            var before = await SnapshotAsync(client, rowIds);
            var todayKeys = (await provider.GetSubRowsAsync(Context(admin, now, null), config, Ct)).Select(r => r.Key).ToArray();

            // Find a later day whose picks differ: the app stayed open past midnight.
            var later = Enumerable.Range(1, 30).Select(d => now.AddDays(d)).ToArray();
            DateTime? nextDay = null;
            foreach (var day in later)
            {
                var keys = (await provider.GetSubRowsAsync(Context(admin, day, null), config, Ct)).Select(r => r.Key);
                if (!keys.Order(StringComparer.Ordinal).SequenceEqual(todayKeys.Order(StringComparer.Ordinal)))
                {
                    nextDay = day;
                    break;
                }
            }

            Assert.NotNull(nextDay);
            for (var i = 0; i < rowIds.Length; i++)
            {
                var found = await Layouts.FindRowAsync(admin, rowIds[i], Ct);
                Assert.NotNull(found);
                var result = await provider.GetItemsAsync(Context(admin, nextDay.Value, found.SubRowKey), config, new HomeRowPaging(0, 100), Ct);
                Assert.Equal(before[i].Title, result.Title);
                Assert.Equal(before[i].Films, result.Items.Select(d => d.Id).ToArray());
            }
        }
        finally
        {
            await Layouts.ResetUserLayoutAsync(admin, CancellationToken.None);
            await DeleteListsAsync(client, listIds);
        }
    }

    private static HomeRowContext Context(User user, DateTime utcNow, string? key)
        => new(user, new DtoOptions(), utcNow) { SubRowKey = key };

    // The list ids are their titles' suffixes: "Title of <id>".
    private static string ListIdOf(string? title) => title!["Title of ".Length..];

    private Guid[] FilmIds(IEnumerable<int> tmdbIds) => tmdbIds.Select(t => Sync.FindFilm(t)!.Id).ToArray();

    private async Task AssertCoherentAsync(IEnumerable<(string? Title, Guid[] Films)> rows)
    {
        var lists = await Store.GetListsAsync(Ct);
        foreach (var row in rows)
        {
            var list = Assert.Single(lists, l => l.Title == row.Title);
            Assert.Equal(FilmIds(list.Films.Select(f => f.TmdbId)), row.Films);
        }
    }

    private async Task<(string? Title, Guid[] Films)[]> SnapshotAsync(HttpClient client, IEnumerable<Guid> rowIds)
    {
        var rows = new List<(string?, Guid[])>();
        foreach (var rowId in rowIds)
        {
            var items = await GetItemsAsync(client, rowId);
            rows.Add((items.Title, items.Items.Select(i => i.Id).ToArray()));
        }

        return rows.ToArray();
    }

    private static async Task<HomeRowItemsDto> GetItemsAsync(HttpClient client, Guid rowId)
    {
        using var response = await client.GetAsync($"HomeRows/{rowId:D}/Items?limit=100", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<HomeRowItemsDto>(JsonDefaults.Options, Ct);
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task<Guid[]> GetDiscoveryRowIdsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("HomeRows/Configuration?expand=true", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<HomeRowConfigurationDto>(JsonDefaults.Options, Ct);
        return dto!.Rows.Where(r => r.Provider == HomeRowProviderIds.MovieDiscovery).Select(r => r.Id).ToArray();
    }

    private static async Task<string> ReshuffleAsync(HttpClient client)
    {
        using var response = await client.PostAsync("Discovery/Picks/Reshuffle", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, Ct);
        return (string)body!["Salt"]!;
    }

    private async Task SaveLayoutAsync(User user)
    {
        var errors = await Layouts.SaveUserLayoutAsync(
            user,
            [new HomeRowInstance(Guid.NewGuid(), HomeRowProviderIds.MovieDiscovery, null, new JsonObject { ["maxRows"] = MaxRows }, true)],
            Ct);
        Assert.Empty(errors);
    }

    private async Task<User> GetAdminAsync(HttpClient client)
        => UserManager.GetUserById((await AuthHelper.GetUserDtoAsync(client)).Id)!;

    // Lists <prefix>-1 .. <prefix>-<count>, two films each from firstTmdbId on.
    private static async Task<string[]> PutListsAsync(HttpClient client, string prefix, int count, int firstTmdbId)
    {
        var ids = Enumerable.Range(1, count).Select(i => prefix + "-" + i).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            await PutListAsync(client, ids[i], firstTmdbId + (2 * i), firstTmdbId + (2 * i) + 1);
        }

        return ids;
    }

    private static async Task PutListAsync(HttpClient client, string listId, params int[] tmdbIds)
    {
        using var response = await client.PutAsJsonAsync(
            "Discovery/Lists/" + listId,
            new { Title = "Title of " + listId, Source = "tests", Films = tmdbIds.Select(t => new { TmdbId = t }).ToArray() },
            JsonDefaults.Options,
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task DeleteListsAsync(HttpClient client, params string[] listIds)
    {
        foreach (var listId in listIds)
        {
            using var response = await client.DeleteAsync("Discovery/Lists/" + listId, CancellationToken.None);
            Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound, $"DELETE {listId}: {response.StatusCode}");
        }
    }

    private async Task<HttpClient> SetUpAsync()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(seed.AccessToken);
        return client;
    }
}
