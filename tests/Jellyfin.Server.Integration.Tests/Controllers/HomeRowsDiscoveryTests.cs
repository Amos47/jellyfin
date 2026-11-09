using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Models.HomeRowDtos;
using Jellyfin.Extensions.Json;
using Jellyfin.HomeRows;
using Jellyfin.Server.Integration.Tests.Discovery;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

/// <summary>
/// The Movie discovery row and expanding rows, on a host seeded by <see cref="DiscoverySeed"/> (its own host, so the
/// seed's movie library doesn't change the standard layout the other HomeRows tests expect).
/// </summary>
public sealed class HomeRowsDiscoveryTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public HomeRowsDiscoveryTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Expand_FalseOrMissing_LayoutUnchanged()
    {
        var client = await SetUpAsync();
        var parentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        try
        {
            await PutListAsync(client, "hr-a", "List A", 801);
            await PutListAsync(client, "hr-b", "List B", 802);
            await PutLayoutAsync(client, Discovery(parentId, 2), Row(nextId, HomeRowProviderIds.NextUp));

            var missing = await GetConfigurationAsync(client, string.Empty);
            var off = await GetConfigurationAsync(client, "?expand=false");

            Assert.Equal(new[] { parentId, nextId }, missing.Rows.Select(r => r.Id));
            Assert.Equal(new[] { parentId, nextId }, off.Rows.Select(r => r.Id));
        }
        finally
        {
            await CleanUpAsync(client, "hr-a", "hr-b");
        }
    }

    [Fact]
    public async Task Expand_True_ThreeListsMaxTwo_TwoRowsWithDistinctStableIds()
    {
        var client = await SetUpAsync();
        var parentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        try
        {
            await PutListAsync(client, "hr-a", "List A", 811);
            await PutListAsync(client, "hr-b", "List B", 812);
            await PutListAsync(client, "hr-c", "List C", 813);
            await PutLayoutAsync(client, Discovery(parentId, 2), Row(nextId, HomeRowProviderIds.NextUp));

            var expanded = await GetConfigurationAsync(client, "?expand=true");
            var again = await GetConfigurationAsync(client, "?expand=true");

            Assert.Equal(new[] { HomeRowProviderIds.MovieDiscovery, HomeRowProviderIds.MovieDiscovery, HomeRowProviderIds.NextUp }, expanded.Rows.Select(r => r.Provider));
            var derived = expanded.Rows.Take(2).Select(r => r.Id).ToArray();
            Assert.Equal(2, derived.Distinct().Count());
            Assert.DoesNotContain(parentId, derived);
            Assert.Equal(expanded.Rows.Select(r => r.Id), again.Rows.Select(r => r.Id));
            Assert.All(expanded.Rows.Take(2), r =>
            {
                Assert.True(r.Enabled);
                Assert.Equal(2, (int)r.Config["maxRows"]!);
            });
            Assert.Equal(nextId, expanded.Rows[2].Id);

            // Each derived row is a different picked list.
            var titles = new string?[2];
            for (var i = 0; i < 2; i++)
            {
                titles[i] = (await GetItemsAsync(client, derived[i])).Title;
            }

            Assert.Equal(2, titles.Distinct().Count());
            Assert.All(titles, t => Assert.Contains(t, new[] { "List A", "List B", "List C" }));
        }
        finally
        {
            await CleanUpAsync(client, "hr-a", "hr-b", "hr-c");
        }
    }

    [Fact]
    public async Task Expand_NoLists_RowRemoved()
    {
        var client = await SetUpAsync();
        var nextId = Guid.NewGuid();
        try
        {
            await PutLayoutAsync(client, Discovery(Guid.NewGuid(), 3), Row(nextId, HomeRowProviderIds.NextUp));

            var expanded = await GetConfigurationAsync(client, "?expand=true");

            Assert.Equal(new[] { nextId }, expanded.Rows.Select(r => r.Id));
        }
        finally
        {
            await CleanUpAsync(client);
        }
    }

    [Fact]
    public async Task Items_DerivedId_ReturnsListTitleAndOrder()
    {
        var client = await SetUpAsync();
        try
        {
            await PutListAsync(client, "hr-order", "In order", 823, 821, 822);
            await PutLayoutAsync(client, Discovery(Guid.NewGuid(), 1));
            var rowId = Assert.Single((await GetConfigurationAsync(client, "?expand=true")).Rows).Id;

            var items = await GetItemsAsync(client, rowId);

            Assert.Equal(rowId, items.RowId);
            Assert.Equal("In order", items.Title);
            Assert.Equal(new[] { 823, 821, 822 }.Select(t => Sync.FindFilm(t)!.Id), items.Items.Select(i => i.Id));
            Assert.Equal(3, items.TotalRecordCount);
        }
        finally
        {
            await CleanUpAsync(client, "hr-order");
        }
    }

    [Fact]
    public async Task Items_OwnedFilm_ReplacesVirtual()
    {
        var client = await SetUpAsync();
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var realFilm = seed.RealFilm;
        var oldTmdb = realFilm.GetProviderId(MetadataProvider.Tmdb);
        try
        {
            realFilm.SetProviderId(MetadataProvider.Tmdb, "832");
            await realFilm.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, Ct);
            await PutListAsync(client, "hr-owned", "Owned", 831, 832);
            await PutLayoutAsync(client, Discovery(Guid.NewGuid(), 1));
            var rowId = Assert.Single((await GetConfigurationAsync(client, "?expand=true")).Rows).Id;

            var items = await GetItemsAsync(client, rowId);

            Assert.Equal(new[] { Sync.FindFilm(831)!.Id, realFilm.Id }, items.Items.Select(i => i.Id));
        }
        finally
        {
            if (oldTmdb is null)
            {
                realFilm.RemoveProviderId(MetadataProvider.Tmdb);
            }
            else
            {
                realFilm.SetProviderId(MetadataProvider.Tmdb, oldTmdb);
            }

            await realFilm.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, Ct);
            await CleanUpAsync(client, "hr-owned");
        }
    }

    [Fact]
    public async Task Items_ParentalLimit_HidesAboveLimit()
    {
        var admin = await SetUpAsync();
        var name = "kid-" + Guid.NewGuid().ToString("N")[..8];
        using (var create = await admin.PostAsJsonAsync("Users/New", new { Name = name, Password = "kid-pw" }, JsonDefaults.Options, Ct))
        {
            create.EnsureSuccessStatusCode();
        }

        var userManager = _factory.Services.GetRequiredService<IUserManager>();
        var kid = userManager.GetUserByName(name)!;
        var localization = _factory.Services.GetRequiredService<ILocalizationManager>();
        kid.MaxParentalRatingScore = localization.GetRatingScore("PG-13", "US")!.Score;
        await userManager.UpdateUserAsync(kid);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthenticateAsync(client, name, "kid-pw"));
        try
        {
            await PutListAsync(admin, "hr-adult", "Adult", 841);
            await PutListAsync(admin, "hr-family", "Family", 842, 843);
            await RateAsync(841, "NC-17");
            await RateAsync(843, "NC-17");
            await PutLayoutAsync(client, Discovery(Guid.NewGuid(), 3));

            // The adult list has nothing the kid may see, so it isn't picked; the family list loses its adult film.
            var rows = (await GetConfigurationAsync(client, "?expand=true")).Rows;
            var items = await GetItemsAsync(client, Assert.Single(rows).Id);

            Assert.Equal("Family", items.Title);
            Assert.Equal(new[] { Sync.FindFilm(842)!.Id }, items.Items.Select(i => i.Id));

            // The detail endpoint agrees with the row.
            using var hidden = await client.GetAsync($"Items/{Sync.FindFilm(843)!.Id:N}", Ct);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
        finally
        {
            await CleanUpAsync(admin, "hr-adult", "hr-family");
        }
    }

    [Fact]
    public async Task Items_RecordsShown()
    {
        var client = await SetUpAsync();
        try
        {
            await PutListAsync(client, "hr-shown", "Shown", 851, 852, 853);
            await PutLayoutAsync(client, Discovery(Guid.NewGuid(), 1));
            var rowId = Assert.Single((await GetConfigurationAsync(client, "?expand=true")).Rows).Id;

            using (var response = await client.GetAsync($"HomeRows/{rowId:D}/Items?limit=2", Ct))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var shown = await _factory.Services.GetRequiredService<IDiscoveryStore>().GetShownAsync(Ct);
            var today = DateOnly.FromDateTime(DateTime.Now);
            Assert.True(shown.ContainsKey(851));
            Assert.True(shown.ContainsKey(852));
            Assert.False(shown.ContainsKey(853));
            Assert.Equal(today, shown[851].FirstShown);
        }
        finally
        {
            await CleanUpAsync(client, "hr-shown");
        }
    }

    [Fact]
    public async Task DefaultLayout_HasNoMovieDiscovery()
    {
        var client = await SetUpAsync();
        try
        {
            await PutListAsync(client, "hr-default", "Default", 861);
            using (var reset = await client.DeleteAsync("HomeRows/Configuration", Ct))
            {
                Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
            }

            var plain = await GetConfigurationAsync(client, string.Empty);
            var expanded = await GetConfigurationAsync(client, "?expand=true");

            Assert.False(plain.IsCustomized);
            Assert.NotEmpty(plain.Rows);
            Assert.DoesNotContain(plain.Rows, r => r.Provider == HomeRowProviderIds.MovieDiscovery);
            Assert.DoesNotContain(expanded.Rows, r => r.Provider == HomeRowProviderIds.MovieDiscovery);
        }
        finally
        {
            await CleanUpAsync(client, "hr-default");
        }
    }

    [Fact]
    public async Task Save_WithExpandedIds_IsRejectedOrIgnored()
    {
        // Ruling: rejected (400). Echoing the expanded layout must not turn one discovery row into several.
        var client = await SetUpAsync();
        var parentId = Guid.NewGuid();
        try
        {
            await PutListAsync(client, "hr-a", "List A", 871);
            await PutListAsync(client, "hr-b", "List B", 872);
            await PutLayoutAsync(client, Discovery(parentId, 2));
            var expanded = await GetConfigurationAsync(client, "?expand=true");
            Assert.Equal(2, expanded.Rows.Count);

            using var put = await client.PutAsJsonAsync("HomeRows/Configuration", new HomeRowConfigurationUpdateDto(expanded.Rows), JsonDefaults.Options, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
            var problem = await put.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, Ct);
            Assert.True(problem!["errors"]!.AsObject().ContainsKey("rows[0].id"));
            Assert.Equal(new[] { parentId }, (await GetConfigurationAsync(client, string.Empty)).Rows.Select(r => r.Id));
            Assert.Equal(2, (await GetConfigurationAsync(client, "?expand=true")).Rows.Count);
        }
        finally
        {
            await CleanUpAsync(client, "hr-a", "hr-b");
        }
    }

    private static HomeRowInstanceDto Discovery(Guid id, int maxRows)
        => new(id, HomeRowProviderIds.MovieDiscovery, null, new JsonObject { ["maxRows"] = maxRows, ["requireTrailer"] = false }, true, false);

    private static HomeRowInstanceDto Row(Guid id, string provider)
        => new(id, provider, null, new JsonObject(), true, false);

    private static async Task PutLayoutAsync(HttpClient client, params HomeRowInstanceDto[] rows)
    {
        using var put = await client.PutAsJsonAsync("HomeRows/Configuration", new HomeRowConfigurationUpdateDto(rows), JsonDefaults.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    private static async Task<HomeRowConfigurationDto> GetConfigurationAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync("HomeRows/Configuration" + query, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<HomeRowConfigurationDto>(JsonDefaults.Options, Ct);
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task<HomeRowItemsDto> GetItemsAsync(HttpClient client, Guid rowId)
    {
        using var response = await client.GetAsync($"HomeRows/{rowId:D}/Items", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<HomeRowItemsDto>(JsonDefaults.Options, Ct);
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task PutListAsync(HttpClient client, string listId, string title, params int[] tmdbIds)
    {
        using var response = await client.PutAsJsonAsync(
            "Discovery/Lists/" + listId,
            new { Title = title, Source = "tests", Films = tmdbIds.Select(t => new { TmdbId = t, ImdbId = (string?)null }).ToArray() },
            JsonDefaults.Options,
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task CleanUpAsync(HttpClient client, params string[] listIds)
    {
        foreach (var listId in listIds)
        {
            using var response = await client.DeleteAsync("Discovery/Lists/" + listId, Ct);
            Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound, $"DELETE {listId}: {response.StatusCode}");
        }

        using var reset = await client.DeleteAsync("HomeRows/Configuration", Ct);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
    }

    private static async Task<string> AuthenticateAsync(HttpClient client, string username, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
        request.Content = JsonContent.Create(new { Username = username, Pw = password }, options: JsonDefaults.Options);
        using var response = await client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, Ct);
        return (string)result!["AccessToken"]!;
    }

    private async Task RateAsync(int tmdbId, string rating)
    {
        var film = Sync.FindFilm(tmdbId)!;
        film.OfficialRating = rating;
        await film.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, Ct);
    }

    private async Task<HttpClient> SetUpAsync()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(seed.AccessToken);
        return client;
    }
}
