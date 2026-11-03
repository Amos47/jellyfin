using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Extensions;

namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// Maps upstream home sections (<see cref="DisplayPreferences.HomeSections"/>) to home row instances.
/// </summary>
public static class UpstreamHomeSectionMapper
{
    /// <summary>
    /// The maximum number of rows in a layout.
    /// </summary>
    public const int MaxRows = 20;

    // Upstream's default home sections, from DisplayPreferencesController.UpdateDisplayPreferences.
    private static readonly HomeSectionType[] _standardOrder =
    [
        HomeSectionType.SmallLibraryTiles,
        HomeSectionType.Resume,
        HomeSectionType.ResumeAudio,
        HomeSectionType.ResumeBook,
        HomeSectionType.LiveTv,
        HomeSectionType.NextUp,
        HomeSectionType.LatestMedia,
    ];

    /// <summary>
    /// Maps home sections to rows. Upstream can hold several sections with the same <see cref="HomeSection.Order"/>;
    /// as in upstream's DisplayPreferences GET, the one with the highest <see cref="HomeSection.Id"/> wins each order.
    /// Orders are then sorted; <see cref="HomeSectionType.None"/> is skipped; <see cref="HomeSectionType.LatestMedia"/>
    /// becomes one <see cref="HomeRowProviderIds.LatestMedia"/> row per latest view; the result is capped at
    /// <see cref="MaxRows"/>. Row ids are deterministic: the MD5 of <c>upstream:{position}:{provider}:{library}</c>,
    /// where position is the section's index among the mapped (non-None) sections, and library is the view id in "N" format, or empty.
    /// </summary>
    /// <param name="sections">The user's home sections; empty means upstream's standard order.</param>
    /// <param name="latestViewIds">The ids of the views that get a latest media row, already filtered.</param>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<HomeRowInstance> Map(IReadOnlyCollection<HomeSection> sections, IReadOnlyList<Guid> latestViewIds)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(latestViewIds);

        IEnumerable<HomeSectionType> types = sections.Count == 0
            ? _standardOrder
            : sections.GroupBy(s => s.Order).OrderBy(g => g.Key).Select(g => g.MaxBy(s => s.Id)!.Type);

        var rows = new List<HomeRowInstance>();
        var position = 0;
        foreach (var type in types)
        {
            if (rows.Count >= MaxRows)
            {
                break;
            }

            if (type == HomeSectionType.LatestMedia)
            {
                foreach (var viewId in latestViewIds)
                {
                    var library = viewId.ToString("N", CultureInfo.InvariantCulture);
                    rows.Add(CreateRow(position, HomeRowProviderIds.LatestMedia, library));
                }
            }
            else if (GetProviderId(type) is { } providerId)
            {
                rows.Add(CreateRow(position, providerId, null));
            }
            else
            {
                continue;
            }

            position++;
        }

        return rows.Count > MaxRows ? rows.GetRange(0, MaxRows) : rows;
    }

    private static HomeRowInstance CreateRow(int position, string providerId, string? library)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"upstream:{position}:{providerId}:{library}").GetMD5();
        var config = new JsonObject();
        if (library is not null)
        {
            config["library"] = library;
        }

        return new HomeRowInstance(id, providerId, null, config, true);
    }

    private static string? GetProviderId(HomeSectionType type) => type switch
    {
        HomeSectionType.SmallLibraryTiles => HomeRowProviderIds.LibraryTiles,
        HomeSectionType.LibraryButtons => HomeRowProviderIds.LibraryButtons,
        HomeSectionType.ActiveRecordings => HomeRowProviderIds.ActiveRecordings,
        HomeSectionType.Resume => HomeRowProviderIds.Resume,
        HomeSectionType.ResumeAudio => HomeRowProviderIds.ResumeAudio,
        HomeSectionType.ResumeBook => HomeRowProviderIds.ResumeBook,
        HomeSectionType.NextUp => HomeRowProviderIds.NextUp,
        HomeSectionType.LiveTv => HomeRowProviderIds.LiveTv,
        _ => null
    };
}
