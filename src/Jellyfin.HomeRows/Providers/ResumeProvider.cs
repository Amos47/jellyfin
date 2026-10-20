using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.HomeRows.Schema;
using Jellyfin.HomeRows.Sources;
using MediaBrowser.Controller.Library;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// A "continue" row for one media type.
/// </summary>
public sealed class ResumeProvider : IHomeRowProvider
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHomeRowItemAdapter _adapter;
    private readonly MediaType _mediaType;
    private readonly string _defaultCardStyle;
    private readonly string? _title;

    private ResumeProvider(
        string id,
        string name,
        string? title,
        string description,
        MediaType mediaType,
        string defaultCardStyle,
        ILibraryManager libraryManager,
        IHomeRowItemAdapter adapter)
    {
        Id = id;
        Name = name;
        Description = description;
        _title = title;
        _mediaType = mediaType;
        _defaultCardStyle = defaultCardStyle;
        _libraryManager = libraryManager;
        _adapter = adapter;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Description { get; }

    /// <summary>
    /// Creates the video, audio and book resume providers.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="adapter">The item adapter.</param>
    /// <returns>The providers.</returns>
    public static IEnumerable<ResumeProvider> CreateAll(ILibraryManager libraryManager, IHomeRowItemAdapter adapter)
    {
        yield return new ResumeProvider(
            HomeRowProviderIds.Resume,
            "Continue Watching (in progress only)",
            "Continue Watching",
            "Videos that are partly watched.",
            MediaType.Video,
            "thumb",
            libraryManager,
            adapter);
        yield return new ResumeProvider(
            HomeRowProviderIds.ResumeAudio,
            "Continue Listening",
            null,
            "Audio that is partly played.",
            MediaType.Audio,
            "square",
            libraryManager,
            adapter);
        yield return new ResumeProvider(
            HomeRowProviderIds.ResumeBook,
            "Continue Reading",
            null,
            "Books that are partly read.",
            MediaType.Book,
            "poster",
            libraryManager,
            adapter);
    }

    /// <inheritdoc />
    public Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
        => Task.FromResult(new HomeRowSchema([CardStyleSetting.Create(_defaultCardStyle)]));

    /// <inheritdoc />
    public Task<HomeRowResult> GetItemsAsync(HomeRowContext context, JsonObject config, HomeRowPaging paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paging);

        var query = ResumeQueryFactory.Create(context.User, _libraryManager, _mediaType, paging.StartIndex, paging.Limit);
        query.DtoOptions = context.DtoOptions;

        var result = _libraryManager.GetItemsResult(query);
        var items = _adapter.ToDtos(result.Items, context);

        return Task.FromResult(new HomeRowResult(items, result.TotalRecordCount, CardStyleSetting.ToPresentation(config, _defaultCardStyle))
        {
            Title = _title
        });
    }
}
