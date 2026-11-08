using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Api.Models.HomeRowDtos;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Extensions.Json;
using Jellyfin.HomeRows;
using Jellyfin.HomeRows.Configuration;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers
{
    public sealed class HomeRowsControllerTests : IClassFixture<JellyfinApplicationFactory>
    {
        private static readonly string[] _allProviderIds =
        [
            HomeRowProviderIds.Resume,
            HomeRowProviderIds.ResumeAudio,
            HomeRowProviderIds.ResumeBook,
            HomeRowProviderIds.NextUp,
            HomeRowProviderIds.LatestMedia,
            HomeRowProviderIds.LibraryTiles,
            HomeRowProviderIds.LibraryButtons,
            HomeRowProviderIds.LiveTv,
            HomeRowProviderIds.ActiveRecordings,
            HomeRowProviderIds.ContinueWatching,
            HomeRowProviderIds.NewReleases,
            HomeRowProviderIds.Genre,
            HomeRowProviderIds.MovieDiscovery
        ];

        // Upstream's standard home sections; latest media maps to one row per library, and the test server has none.
        private static readonly string[] _standardOrder =
        [
            HomeRowProviderIds.LibraryTiles,
            HomeRowProviderIds.Resume,
            HomeRowProviderIds.ResumeAudio,
            HomeRowProviderIds.ResumeBook,
            HomeRowProviderIds.LiveTv,
            HomeRowProviderIds.NextUp
        ];

        private static string? _accessToken;
        private readonly JellyfinApplicationFactory _factory;

        public HomeRowsControllerTests(JellyfinApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Providers_ListsAllThirteenIds()
        {
            var client = await CreateClientAsync();

            using var response = await client.GetAsync("HomeRows/Providers", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dto = await response.Content.ReadFromJsonAsync<HomeRowProvidersDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(dto);
            Assert.Equal(1, dto.ApiVersion);
            Assert.Equal(_allProviderIds.Order(), dto.Providers.Select(p => p.Id).Order());
            Assert.All(dto.Providers, p =>
            {
                Assert.NotNull(p.Schema);
                Assert.NotNull(p.Defaults);
            });
        }

        [Fact]
        public async Task Configuration_FreshUser_StandardDefault()
        {
            var client = await CreateClientAsync();
            await ResetAsync(client);

            var dto = await GetConfigurationAsync(client);

            Assert.Equal(HomeRowLayoutSource.StandardDefault, dto.Source);
            Assert.False(dto.IsCustomized);
            Assert.Equal(_standardOrder, dto.Rows.Select(r => r.Provider));
            Assert.All(dto.Rows, r => Assert.True(r.IsAvailable));
        }

        [Fact]
        public async Task Configuration_PutThenGet_IsCustomized()
        {
            var client = await CreateClientAsync();
            var rowId = Guid.NewGuid();
            var update = new HomeRowConfigurationUpdateDto(
            [
                new HomeRowInstanceDto(rowId, HomeRowProviderIds.NextUp, "My next up", new JsonObject { ["enableRewatching"] = true, ["nextUpCutoffDays"] = 30, ["dropped"] = new JsonObject { ["nested"] = new JsonArray(1, 2) } }, true, false),
                new HomeRowInstanceDto(Guid.Empty, HomeRowProviderIds.Resume, null, new JsonObject(), false, false)
            ]);

            using var put = await client.PutAsJsonAsync("HomeRows/Configuration", update, JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var saved = await put.Content.ReadFromJsonAsync<HomeRowConfigurationDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(saved);
            Assert.True(saved.IsCustomized);

            var dto = await GetConfigurationAsync(client);

            Assert.Equal(HomeRowLayoutSource.User, dto.Source);
            Assert.True(dto.IsCustomized);
            Assert.Equal(2, dto.Rows.Count);
            Assert.Equal(rowId, dto.Rows[0].Id);
            Assert.Equal("My next up", dto.Rows[0].Title);
            Assert.True(dto.Rows[0].IsAvailable);
            Assert.True((bool)dto.Rows[0].Config["enableRewatching"]!);
            Assert.Equal(30, (int)dto.Rows[0].Config["nextUpCutoffDays"]!);
            Assert.False(dto.Rows[0].Config.ContainsKey("dropped"));
            Assert.Equal(HomeRowProviderIds.Resume, dto.Rows[1].Provider);
            Assert.False(dto.Rows[1].Id.Equals(Guid.Empty));
            Assert.False(dto.Rows[1].Enabled);
        }

        [Fact]
        public async Task Configuration_PutInvalid_ValidationProblem()
        {
            var client = await CreateClientAsync();
            var update = new HomeRowConfigurationUpdateDto(
            [
                new HomeRowInstanceDto(Guid.NewGuid(), HomeRowProviderIds.NextUp, null, new JsonObject(), true, true),
                new HomeRowInstanceDto(Guid.NewGuid(), HomeRowProviderIds.Genre, null, new JsonObject(), true, true)
            ]);

            using var put = await client.PutAsJsonAsync("HomeRows/Configuration", update, JsonDefaults.Options, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
            var problem = await put.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(problem);
            var errors = Assert.IsType<JsonObject>(problem["errors"]);
            Assert.Contains(errors, e => e.Key.StartsWith("rows[1].", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Configuration_Delete_ReturnsToDefault()
        {
            var client = await CreateClientAsync();
            var update = new HomeRowConfigurationUpdateDto(
            [
                new HomeRowInstanceDto(Guid.NewGuid(), HomeRowProviderIds.NextUp, null, new JsonObject(), true, true)
            ]);
            using (var put = await client.PutAsJsonAsync("HomeRows/Configuration", update, JsonDefaults.Options, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            }

            using var delete = await client.DeleteAsync("HomeRows/Configuration", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            var dto = await GetConfigurationAsync(client);
            Assert.Equal(HomeRowLayoutSource.StandardDefault, dto.Source);
            Assert.False(dto.IsCustomized);
            Assert.Equal(_standardOrder, dto.Rows.Select(r => r.Provider));
        }

        [Fact]
        public async Task RowItems_ForDefaultRow_Ok()
        {
            var client = await CreateClientAsync();
            await ResetAsync(client);
            var configuration = await GetConfigurationAsync(client);
            var row = configuration.Rows[0];

            using var response = await client.GetAsync($"HomeRows/{row.Id}/Items?startIndex=0&limit=20&fields=Overview&enableImageTypes=Primary,Thumb", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dto = await response.Content.ReadFromJsonAsync<HomeRowItemsDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(dto);
            Assert.Equal(row.Id, dto.RowId);
            Assert.Equal(0, dto.StartIndex);
            Assert.NotNull(dto.Items);
            Assert.NotNull(dto.Presentation);
        }

        [Fact]
        public async Task RowItems_UnknownRowOrBadLimit_Rejected()
        {
            var client = await CreateClientAsync();

            using var unknown = await client.GetAsync($"HomeRows/{Guid.NewGuid()}/Items", TestContext.Current.CancellationToken);
            using var badLimit = await client.GetAsync($"HomeRows/{Guid.NewGuid()}/Items?limit=101", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
        }

        [Fact]
        public async Task DefaultConfiguration_PutAsAdmin_ThenGetReturnsIt()
        {
            // A server of its own: an admin default would leak into the other tests.
            using var factory = new JellyfinApplicationFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));

            using (var none = await client.GetAsync("HomeRows/DefaultConfiguration", TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
            }

            var rowId = Guid.NewGuid();
            HomeRowInstanceDto[] rows =
            [
                new HomeRowInstanceDto(rowId, HomeRowProviderIds.ContinueWatching, "Keep watching", new JsonObject(), true, false)
            ];
            using (var put = await client.PutAsJsonAsync("HomeRows/DefaultConfiguration", new HomeRowConfigurationUpdateDto(rows), JsonDefaults.Options, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            }

            using var get = await client.GetAsync("HomeRows/DefaultConfiguration", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var saved = await get.Content.ReadFromJsonAsync<HomeRowInstanceDto[]>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(saved);
            var row = Assert.Single(saved);
            Assert.Equal(rowId, row.Id);
            Assert.Equal(HomeRowProviderIds.ContinueWatching, row.Provider);
            Assert.Equal("Keep watching", row.Title);
            Assert.True(row.IsAvailable);

            // The admin has no layout of their own, so they inherit the new default.
            var configuration = await GetConfigurationAsync(client);
            Assert.Equal(HomeRowLayoutSource.AdminDefault, configuration.Source);
            Assert.Equal(rowId, Assert.Single(configuration.Rows).Id);
        }

        [Fact]
        public async Task DefaultConfiguration_Delete_RemovesTheDefaultForAdminsOnly()
        {
            using var factory = new JellyfinApplicationFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));

            HomeRowInstanceDto[] rows =
            [
                new HomeRowInstanceDto(Guid.NewGuid(), HomeRowProviderIds.ContinueWatching, "Keep watching", new JsonObject(), true, false)
            ];
            using (var put = await client.PutAsJsonAsync("HomeRows/DefaultConfiguration", new HomeRowConfigurationUpdateDto(rows), JsonDefaults.Options, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            }

            Assert.Equal(HomeRowLayoutSource.AdminDefault, (await GetConfigurationAsync(client)).Source);

            // A regular user may not remove it.
            using (var userClient = factory.CreateClient())
            {
                using var create = await client.PostAsJsonAsync("Users/New", new CreateUserByName { Name = "regular", Password = "regular-pw" }, JsonDefaults.Options, TestContext.Current.CancellationToken);
                create.EnsureSuccessStatusCode();
                userClient.DefaultRequestHeaders.AddAuthHeader(await AuthenticateAsync(userClient, "regular", "regular-pw"));

                using var forbidden = await userClient.DeleteAsync("HomeRows/DefaultConfiguration", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }

            using (var get = await client.GetAsync("HomeRows/DefaultConfiguration", TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            }

            using (var delete = await client.DeleteAsync("HomeRows/DefaultConfiguration", TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            }

            using (var none = await client.GetAsync("HomeRows/DefaultConfiguration", TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
            }

            var configuration = await GetConfigurationAsync(client);
            Assert.NotEqual(HomeRowLayoutSource.AdminDefault, configuration.Source);
            Assert.Equal(_standardOrder, configuration.Rows.Select(r => r.Provider));
        }

        [Fact]
        public async Task Swagger_ContainsHomeRowsPaths()
        {
            var client = _factory.CreateClient();

            using var response = await client.GetAsync("/api-docs/openapi.json", TestContext.Current.CancellationToken);

            response.EnsureSuccessStatusCode();
            var spec = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("/HomeRows/Providers", spec, StringComparison.Ordinal);
            Assert.Contains("/HomeRows/{rowId}/Items", spec, StringComparison.Ordinal);
            Assert.Contains("/HomeRows/DefaultConfiguration", spec, StringComparison.Ordinal);

            // JsonObject properties are free-form objects, not maps of JsonNode with its Parent and Root properties.
            var schemas = JsonNode.Parse(spec)!["components"]!["schemas"]!.AsObject();
            Assert.False(schemas.ContainsKey("JsonNode"));
            AssertFreeFormObject(schemas["HomeRowProviderDto"]!["properties"]!["Schema"]);
            AssertFreeFormObject(schemas["HomeRowProviderDto"]!["properties"]!["Defaults"]);
            AssertFreeFormObject(schemas["HomeRowInstanceDto"]!["properties"]!["Config"]);
        }

        private static void AssertFreeFormObject(JsonNode? property)
        {
            Assert.NotNull(property);
            Assert.Equal("object", (string?)property["type"]);
            Assert.Null(property["properties"]);
            Assert.DoesNotContain("$ref", property.ToJsonString(), StringComparison.Ordinal);
        }

        private static async Task<HomeRowConfigurationDto> GetConfigurationAsync(HttpClient client)
        {
            using var response = await client.GetAsync("HomeRows/Configuration", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dto = await response.Content.ReadFromJsonAsync<HomeRowConfigurationDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(dto);
            return dto;
        }

        // The fixture is shared by this class: reset in case another test customized the layout first.
        private static async Task ResetAsync(HttpClient client)
        {
            using var response = await client.DeleteAsync("HomeRows/Configuration", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        private static async Task<string> AuthenticateAsync(HttpClient client, string username, string password)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
            request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
            request.Content = JsonContent.Create(new AuthenticateUserByName { Username = username, Pw = password }, options: JsonDefaults.Options);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            return (string)result!["AccessToken"]!;
        }

        private async Task<HttpClient> CreateClientAsync()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
            return client;
        }
    }
}
