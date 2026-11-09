using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// The most rows one row of an <see cref="IExpandingHomeRowProvider"/> expands into.
    /// </summary>
    public const int MaxExpandedRows = 10;

    private const string DerivedRowPrefixText = "derived-row";
    private const int DerivedRowPrefixLength = 4;
    private const string UpstreamSettingsClient = "emby";
    private const string UpstreamSettingsId = "usersettings";
    private const string UserListRowMarker = "home-rows:user-list-row";

    // The id of the Saved row of a layout that isn't stored: the same for every user and read.
    private static readonly Guid _defaultUserListRowId = "default:user-list".GetMD5();

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
        // Neither is stored, so the Saved row is added on every read (never written, here or to jellyfin.db).
        return sections.Any(s => s.Type != HomeSectionType.None)
            ? ToLayout(HomeRowLayoutSource.UpstreamSections, WithDefaultUserListRow(UpstreamHomeSectionMapper.Map(sections.ToArray(), latestViewIds)))
            : ToLayout(HomeRowLayoutSource.StandardDefault, WithDefaultUserListRow(UpstreamHomeSectionMapper.Map([], latestViewIds)));
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
    public async Task<IReadOnlyList<IReadOnlyList<HomeRowInstance>>> GetUserLayoutsAsync(CancellationToken ct)
    {
        var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var stored = await context.HomeRowConfigurations.AsNoTracking()
                .Select(c => new { c.UserId, c.RowsJson })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return stored
                .Select(c => TryDeserialize(c.RowsJson, c.UserId))
                .OfType<List<HomeRowInstance>>()
                .ToList<IReadOnlyList<HomeRowInstance>>();
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
        if (layout.Rows.FirstOrDefault(r => r.Row.Id.Equals(rowId)) is { } row)
        {
            return row;
        }

        // A derived id of an expanded row (it carries the parent's prefix): the parent row, with the key of the sub-row
        // it names. Every key the provider can still serve matches, not only today's, so a client holding an older layout
        // gets the same content. A key it can no longer serve (a deleted list) gets HomeSubRow.UnknownKey: an empty row.
        // A provider that fails here throws (the request answers 500): an empty row would look like a deleted list.
        var context = new HomeRowContext(user, new DtoOptions(), DateTime.UtcNow);
        foreach (var parent in layout.Rows.Where(r => IsExpanding(r) && HasDerivedRowPrefix(r.Row.Id, rowId)))
        {
            var keys = await GetResolvableSubRowKeysAsync(context, parent.Row, ct).ConfigureAwait(false);
            var key = keys.FirstOrDefault(k => !string.IsNullOrEmpty(k) && GetDerivedRowId(parent.Row.Id, k).Equals(rowId));
            return parent with { SubRowKey = key ?? HomeSubRow.UnknownKey };
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<EffectiveHomeRowLayout> ExpandAsync(User user, EffectiveHomeRowLayout layout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(layout);

        var context = new HomeRowContext(user, new DtoOptions(), DateTime.UtcNow);
        var rows = new List<EffectiveHomeRow>(layout.Rows.Count);
        foreach (var row in layout.Rows)
        {
            if (!IsExpanding(row))
            {
                rows.Add(row);
                continue;
            }

            var provider = (IExpandingHomeRowProvider)_providerFactory.Get(row.Row.Provider)!;
            IReadOnlyList<HomeSubRow> subRows;
            try
            {
                var schema = await provider.GetSchemaAsync(context, ct).ConfigureAwait(false);
                var config = HomeRowConfigValidator.Validate(schema, row.Row.Config).Config;
                subRows = await provider.GetSubRowsAsync(context, config, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One failing provider must not break the layout: its row is left out.
                _logger.LogError(ex, "Expanding home row {RowId} of provider {ProviderId} failed", row.Row.Id, row.Row.Provider);
                subRows = [];
            }

            // An empty key is HomeSubRow.UnknownKey, never a real sub-row.
            foreach (var subRow in subRows.Where(r => !string.IsNullOrEmpty(r.Key)).DistinctBy(r => r.Key, StringComparer.Ordinal).Take(MaxExpandedRows))
            {
                // Title = null: each derived row is named by its own list title from /Items, not the parent's custom title.
                rows.Add(row with { Row = row.Row with { Id = GetDerivedRowId(row.Row.Id, subRow.Key), Title = null }, SubRowKey = subRow.Key });
            }
        }

        return layout with { Rows = rows };
    }

    /// <inheritdoc />
    public async Task<bool> InsertUserListRowsOnceAsync(CancellationToken ct)
    {
        var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            // One transaction: the marker and every layout change land together, or none of them.
            var transaction = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                if (await context.Markers.AnyAsync(m => m.Id == UserListRowMarker, ct).ConfigureAwait(false))
                {
                    return false;
                }

                var now = DateTime.UtcNow;
                foreach (var entity in await context.HomeRowConfigurations.ToListAsync(ct).ConfigureAwait(false))
                {
                    if (WithUserListRow(entity.RowsJson, entity.UserId) is { } json)
                    {
                        entity.RowsJson = json;
                        entity.DateModified = now;
                    }
                }

                foreach (var entity in await context.HomeRowDefaults.ToListAsync(ct).ConfigureAwait(false))
                {
                    if (WithUserListRow(entity.RowsJson, "admin default") is { } json)
                    {
                        entity.RowsJson = json;
                        entity.DateModified = now;
                    }
                }

                context.Markers.Add(new CustomMarker { Id = UserListRowMarker, DateApplied = now });
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return true;
            }
        }
    }

    /// <summary>
    /// Gets the id of the row of an expanded row with sub-row key <paramref name="key"/>, as the 16 bytes of a GUID
    /// (<see cref="Guid.ToByteArray()"/> order): bytes 0 to 3 are the first 4 bytes of the MD5 of the 16 bytes of
    /// <paramref name="parentId"/> followed by the UTF-8 bytes of <c>derived-row</c> (the parent's prefix, the same
    /// for all its derived rows); bytes 4 to 15 are bytes 4 to 15 of the MD5 of the 16 bytes of
    /// <paramref name="parentId"/> followed by the UTF-8 bytes of <paramref name="key"/>. The id names the sub-row's
    /// content (Movie discovery: its list), not its position, so it is the same whatever else is shown that day; the
    /// prefix tells that an id belongs to the parent even when its key is gone.
    /// </summary>
    /// <param name="parentId">The id of the layout row.</param>
    /// <param name="key">The <see cref="HomeSubRow.Key"/> of the derived row.</param>
    /// <returns>The derived row id.</returns>
    public static Guid GetDerivedRowId(Guid parentId, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        Span<byte> id = stackalloc byte[16];
        HashWithParent(parentId, key, id);
        HashWithParent(parentId, DerivedRowPrefixText, stackalloc byte[16])[..DerivedRowPrefixLength].CopyTo(id);
        return new Guid(id);
    }

    /// <summary>
    /// Gets a new row id that carries no expanding row's derived-row prefix, so a saved row is never taken for an expanded one.
    /// </summary>
    /// <param name="expandingIds">The ids of the expanding rows.</param>
    /// <param name="next">Draws a candidate id.</param>
    /// <returns>The new row id.</returns>
    public static Guid NewRowId(IEnumerable<Guid> expandingIds, Func<Guid> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var parents = expandingIds.ToList();
        Guid id;
        do
        {
            id = next();
        }
        while (parents.Any(parentId => HasDerivedRowPrefix(parentId, id)));

        return id;
    }

    /// <summary>
    /// Gets whether an id carries the prefix of the rows derived from <paramref name="parentId"/>
    /// (see <see cref="GetDerivedRowId"/>).
    /// </summary>
    /// <param name="parentId">The id of the layout row.</param>
    /// <param name="rowId">The id to check.</param>
    /// <returns>Whether <paramref name="rowId"/> looks like a row derived from <paramref name="parentId"/>.</returns>
    public static bool HasDerivedRowPrefix(Guid parentId, Guid rowId)
    {
        Span<byte> id = stackalloc byte[16];
        rowId.TryWriteBytes(id);
        return HashWithParent(parentId, DerivedRowPrefixText, stackalloc byte[16])[..DerivedRowPrefixLength].SequenceEqual(id[..DerivedRowPrefixLength]);
    }

    // MD5 of the 16 parent id bytes followed by the UTF-8 text, written to and returned as hash.
    private static Span<byte> HashWithParent(Guid parentId, string text, Span<byte> hash)
    {
        var bytes = new byte[16 + Encoding.UTF8.GetByteCount(text)];
        parentId.TryWriteBytes(bytes);
        Encoding.UTF8.GetBytes(text, bytes.AsSpan(16));

        // Not security related: MD5 only derives a stable row id, like upstream's GetMD5.
#pragma warning disable CA5351
        MD5.HashData(bytes, hash);
#pragma warning restore CA5351
        return hash;
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

    // The keys an expanding row's provider can still serve, with the row's config validated against its schema.
    private async Task<IReadOnlyList<string>> GetResolvableSubRowKeysAsync(HomeRowContext context, HomeRowInstance row, CancellationToken ct)
    {
        var provider = (IExpandingHomeRowProvider)_providerFactory.Get(row.Provider)!;
        var schema = await provider.GetSchemaAsync(context, ct).ConfigureAwait(false);
        var config = HomeRowConfigValidator.Validate(schema, row.Config).Config;
        return await provider.GetResolvableSubRowKeysAsync(context, config, ct).ConfigureAwait(false);
    }

    // A layout that isn't stored, with the Saved row after its first Movie discovery row (else at the end), unless it
    // already has one or is full.
    private static IReadOnlyList<HomeRowInstance> WithDefaultUserListRow(IReadOnlyList<HomeRowInstance> rows)
    {
        if (rows.Count >= UpstreamHomeSectionMapper.MaxRows || rows.Any(IsUserListRow))
        {
            return rows;
        }

        var list = rows.ToList();
        InsertAfterDiscovery(list, new HomeRowInstance(_defaultUserListRowId, HomeRowProviderIds.UserList, null, new JsonObject(), true));
        return list;
    }

    private static bool IsUserListRow(HomeRowInstance row)
        => string.Equals(row.Provider, HomeRowProviderIds.UserList, StringComparison.Ordinal);

    private static void InsertAfterDiscovery(List<HomeRowInstance> rows, HomeRowInstance row)
    {
        var discovery = rows.FindIndex(r => string.Equals(r.Provider, HomeRowProviderIds.MovieDiscovery, StringComparison.Ordinal));
        rows.Insert(discovery < 0 ? rows.Count : discovery + 1, row);
    }

    // The layout with a user-list row after its first Movie discovery row (else at the end), or null to leave it as it
    // is: it doesn't parse, already has one, or is full (logged, since that user won't see Saved until they make room).
    private string? WithUserListRow(string json, object owner)
    {
        if (TryDeserialize(json, owner) is not { } rows || rows.Any(IsUserListRow))
        {
            return null;
        }

        if (rows.Count >= UpstreamHomeSectionMapper.MaxRows)
        {
            _logger.LogWarning("Not adding the Saved row to the home row layout of {Owner}: it already has {Count} rows, the most a layout can hold", owner, rows.Count);
            return null;
        }

        var expandingIds = rows
            .Where(r => _providerFactory.Get(r.Provider) is IExpandingHomeRowProvider
                || string.Equals(r.Provider, HomeRowProviderIds.MovieDiscovery, StringComparison.Ordinal))
            .Select(r => r.Id);
        InsertAfterDiscovery(rows, new HomeRowInstance(NewRowId(expandingIds, Guid.NewGuid), HomeRowProviderIds.UserList, null, new JsonObject(), true));
        return Serialize(rows);
    }

    private bool IsExpanding(EffectiveHomeRow row)
        => row.Row.Enabled && row.IsAvailable && _providerFactory.Get(row.Row.Provider) is IExpandingHomeRowProvider;

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

        // Ruling: the ids of expanded rows are rejected, so saving the display view can't multiply an expanding row.
        // Any id with an expanding row's derived-row prefix counts, whatever its key.
        var expandingIds = stored.Concat(rows)
            .Where(r => !string.IsNullOrEmpty(r.Provider) && _providerFactory.Get(r.Provider) is IExpandingHomeRowProvider)
            .Select(r => r.Id)
            .ToHashSet();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var id = row.Id.IsEmpty() ? NewRowId(expandingIds, Guid.NewGuid) : row.Id;
            if (!ids.Add(id))
            {
                errors.Add(new HomeRowSaveError(i, "id", "The row id is used by another row."));
            }
            else if (expandingIds.Any(parentId => HasDerivedRowPrefix(parentId, id)))
            {
                errors.Add(new HomeRowSaveError(i, "id", "The row id is the id of an expanded row; save the layout from HomeRows/Configuration without expand."));
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
