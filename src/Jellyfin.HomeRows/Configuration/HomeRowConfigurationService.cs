using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Custom.Database;
using Jellyfin.Custom.Database.Entities;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// Stores home row layouts in <see cref="CustomDbContext"/>.
/// </summary>
/// <remarks>
/// <see cref="CustomHomeRowConfiguration.RowsJson"/> and <see cref="CustomHomeRowDefault.RowsJson"/> hold a JSON
/// array of rows in display order, each
/// <c>{"id":"&lt;guid, D format&gt;","provider":"&lt;provider id&gt;","title":"&lt;title&gt;"|null,"config":{...},"enabled":true|false}</c>.
/// A missing <c>enabled</c> reads as <c>true</c>. A record that does not parse, or has a row without an id or provider,
/// is treated as absent: reads fall through to the next precedence level, and saves overwrite it.
/// </remarks>
public class HomeRowConfigurationService : IHomeRowConfigurationService
{
    /// <summary>
    /// The maximum length of a row title.
    /// </summary>
    public const int MaxTitleLength = 100;

    private const string UpstreamSettingsClient = "emby";
    private const string UpstreamSettingsId = "usersettings";

    private readonly IDbContextFactory<CustomDbContext> _dbFactory;
    private readonly IDbContextFactory<JellyfinDbContext> _jellyfinDbFactory;
    private readonly IHomeRowProviderFactory _providerFactory;
    private readonly IUserViewManager _userViewManager;
    private readonly ILogger<HomeRowConfigurationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeRowConfigurationService"/> class.
    /// </summary>
    /// <param name="dbFactory">The fork database context factory.</param>
    /// <param name="jellyfinDbFactory">The upstream database context factory, only read from (upstream home sections).</param>
    /// <param name="providerFactory">The home row provider factory.</param>
    /// <param name="userViewManager">The user view manager.</param>
    /// <param name="logger">The logger.</param>
    public HomeRowConfigurationService(
        IDbContextFactory<CustomDbContext> dbFactory,
        IDbContextFactory<JellyfinDbContext> jellyfinDbFactory,
        IHomeRowProviderFactory providerFactory,
        IUserViewManager userViewManager,
        ILogger<HomeRowConfigurationService> logger)
    {
        _dbFactory = dbFactory;
        _jellyfinDbFactory = jellyfinDbFactory;
        _providerFactory = providerFactory;
        _userViewManager = userViewManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EffectiveHomeRowLayout> GetEffectiveLayoutAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var own = await context.HomeRowConfigurations.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserId.Equals(user.Id), ct)
                .ConfigureAwait(false);
            if (own is not null && TryDeserialize(own.RowsJson, user.Id) is { } ownRows)
            {
                return ToLayout(HomeRowLayoutSource.User, ownRows);
            }

            if (await GetAdminDefaultRowsAsync(context, ct).ConfigureAwait(false) is { } defaultRows)
            {
                return ToLayout(HomeRowLayoutSource.AdminDefault, defaultRows);
            }
        }

        var sections = await GetUpstreamSectionsAsync(user.Id, ct).ConfigureAwait(false);
        var latestViewIds = LatestViews.Get(user, _userViewManager).Select(v => v.Id).ToArray();
        return sections.Any(s => s.Type != HomeSectionType.None)
            ? ToLayout(HomeRowLayoutSource.UpstreamSections, UpstreamHomeSectionMapper.Map(sections.ToArray(), latestViewIds))
            : ToLayout(HomeRowLayoutSource.StandardDefault, UpstreamHomeSectionMapper.Map([], latestViewIds));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HomeRowSaveError>> SaveUserLayoutAsync(User user, IReadOnlyList<HomeRowInstance> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(rows);

        var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var entity = await context.HomeRowConfigurations
                .FirstOrDefaultAsync(c => c.UserId.Equals(user.Id), ct)
                .ConfigureAwait(false);
            // The baseline for rows with an unregistered provider is the layout the user was shown: their own, or else
            // the admin default they inherit. A corrupt own record gives an empty baseline, so it can be overwritten.
            var stored = entity is null
                ? await GetAdminDefaultRowsAsync(context, ct).ConfigureAwait(false) ?? []
                : TryDeserialize(entity.RowsJson, user.Id) ?? [];
            var (normalized, errors) = await NormalizeAsync(user, rows, stored, ct).ConfigureAwait(false);
            if (errors.Count > 0)
            {
                return errors;
            }

            var json = Serialize(normalized);
            if (entity is null)
            {
                context.HomeRowConfigurations.Add(new CustomHomeRowConfiguration
                {
                    UserId = user.Id,
                    RowsJson = json,
                    DateModified = DateTime.UtcNow
                });
            }
            else
            {
                entity.RowsJson = json;
                entity.DateModified = DateTime.UtcNow;
            }

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task ResetUserLayoutAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.HomeRowConfigurations
                .Where(c => c.UserId.Equals(user.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HomeRowInstance>?> GetAdminDefaultAsync(CancellationToken ct)
    {
        var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await GetAdminDefaultRowsAsync(context, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HomeRowSaveError>> SaveAdminDefaultAsync(User admin, IReadOnlyList<HomeRowInstance> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(rows);

        var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var entity = await GetDefaultEntityAsync(context, false, ct).ConfigureAwait(false);
            var stored = entity is null ? [] : TryDeserialize(entity.RowsJson, "admin default") ?? [];
            var (normalized, errors) = await NormalizeAsync(admin, rows, stored, ct).ConfigureAwait(false);
            if (errors.Count > 0)
            {
                return errors;
            }

            var json = Serialize(normalized);
            if (entity is null)
            {
                context.HomeRowDefaults.Add(new CustomHomeRowDefault
                {
                    Id = 1,
                    RowsJson = json,
                    DateModified = DateTime.UtcNow,
                    ModifiedByUserId = admin.Id
                });
            }
            else
            {
                entity.RowsJson = json;
                entity.DateModified = DateTime.UtcNow;
                entity.ModifiedByUserId = admin.Id;
            }

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task ResetAdminDefaultAsync(CancellationToken ct)
    {
        var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.HomeRowDefaults
                .Where(d => d.Id == 1)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<EffectiveHomeRow?> FindRowAsync(User user, Guid rowId, CancellationToken ct)
    {
        var layout = await GetEffectiveLayoutAsync(user, ct).ConfigureAwait(false);
        return layout.Rows.FirstOrDefault(r => r.Row.Id.Equals(rowId));
    }

    private static Task<CustomHomeRowDefault?> GetDefaultEntityAsync(CustomDbContext context, bool readOnly, CancellationToken ct)
    {
        var query = readOnly ? context.HomeRowDefaults.AsNoTracking() : context.HomeRowDefaults;
        return query.FirstOrDefaultAsync(d => d.Id == 1, ct);
    }

    private static string Serialize(IEnumerable<HomeRowInstance> rows)
        => JsonSerializer.Serialize(rows.Select(r => new StoredRow
        {
            Id = r.Id,
            Provider = r.Provider,
            Title = r.Title,
            Config = r.Config,
            Enabled = r.Enabled
        }));

    private async Task<List<HomeRowInstance>?> GetAdminDefaultRowsAsync(CustomDbContext context, CancellationToken ct)
    {
        var entity = await GetDefaultEntityAsync(context, true, ct).ConfigureAwait(false);
        return entity is null ? null : TryDeserialize(entity.RowsJson, "admin default");
    }

    private async Task<IReadOnlyCollection<HomeSection>> GetUpstreamSectionsAsync(Guid userId, CancellationToken ct)
    {
        // Read directly, untracked: IDisplayPreferencesManager.GetDisplayPreferences inserts a row when there is none.
        var itemId = UpstreamSettingsId.GetMD5();
        var context = await _jellyfinDbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var preferences = await context.DisplayPreferences.AsNoTracking()
                .Include(p => p.HomeSections)
                .FirstOrDefaultAsync(p => p.UserId.Equals(userId) && p.Client == UpstreamSettingsClient && p.ItemId.Equals(itemId), ct)
                .ConfigureAwait(false);
            return preferences?.HomeSections.ToArray() ?? [];
        }
    }

    private List<HomeRowInstance>? TryDeserialize(string json, object owner)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredRow?>>(json)
                ?? throw new JsonException("The layout is null.");
            var rows = new List<HomeRowInstance>(stored.Count);
            foreach (var row in stored)
            {
                if (row is null || row.Id.IsEmpty() || string.IsNullOrEmpty(row.Provider))
                {
                    throw new JsonException("A row has no id or provider.");
                }

                rows.Add(new HomeRowInstance(row.Id, row.Provider, row.Title, row.Config ?? new JsonObject(), row.Enabled));
            }

            return rows;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Ignoring the corrupt home row layout of {Owner}", owner);
            return null;
        }
    }

    private EffectiveHomeRowLayout ToLayout(HomeRowLayoutSource source, IEnumerable<HomeRowInstance> rows)
        => new(source, rows.Select(r => new EffectiveHomeRow(r, _providerFactory.Get(r.Provider) is not null)).ToArray());

    private async Task<(List<HomeRowInstance> Rows, List<HomeRowSaveError> Errors)> NormalizeAsync(
        User user,
        IReadOnlyList<HomeRowInstance> rows,
        IReadOnlyList<HomeRowInstance> stored,
        CancellationToken ct)
    {
        var normalized = new List<HomeRowInstance>(rows.Count);
        var errors = new List<HomeRowSaveError>();
        if (rows.Count > UpstreamHomeSectionMapper.MaxRows)
        {
            errors.Add(new HomeRowSaveError(-1, "rows", $"A layout has at most {UpstreamHomeSectionMapper.MaxRows} rows."));
            return (normalized, errors);
        }

        var context = new HomeRowContext(user, new DtoOptions(), DateTime.UtcNow);
        var schemas = new Dictionary<string, HomeRowSchema>(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var id = row.Id.IsEmpty() ? Guid.NewGuid() : row.Id;
            if (!ids.Add(id))
            {
                errors.Add(new HomeRowSaveError(i, "id", "The row id is used by another row."));
            }

            var title = row.Title?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                title = null;
            }
            else if (title.Length > MaxTitleLength)
            {
                errors.Add(new HomeRowSaveError(i, "title", $"Must be at most {MaxTitleLength} characters."));
            }

            JsonObject config;
            var provider = string.IsNullOrEmpty(row.Provider) ? null : _providerFactory.Get(row.Provider);
            if (provider is not null)
            {
                if (!schemas.TryGetValue(provider.Id, out var schema))
                {
                    schema = await provider.GetSchemaAsync(context, ct).ConfigureAwait(false);
                    schemas[provider.Id] = schema;
                }

                var result = HomeRowConfigValidator.Validate(schema, row.Config ?? new JsonObject());
                errors.AddRange(result.Errors.Select(e => new HomeRowSaveError(i, e.Key, e.Message)));
                config = result.Config;
            }
            else if (!string.IsNullOrEmpty(row.Provider)
                && stored.Any(s => s.Id.Equals(id) && string.Equals(s.Provider, row.Provider, StringComparison.Ordinal)))
            {
                // The provider is gone, but the row was stored with it: keep it as sent, so it works again if the provider returns.
                config = row.Config?.DeepClone().AsObject() ?? new JsonObject();
            }
            else
            {
                errors.Add(new HomeRowSaveError(i, "provider", $"Unknown provider '{row.Provider}'."));
                continue;
            }

            normalized.Add(new HomeRowInstance(id, row.Provider, title, config, row.Enabled));
        }

        return (normalized, errors);
    }

    private sealed class StoredRow
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("provider")]
        public string? Provider { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("config")]
        public JsonObject? Config { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
    }
}
