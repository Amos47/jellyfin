using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// The "My Media" rows: the user's libraries as tiles or as small buttons.
/// </summary>
public sealed class LibraryTilesProvider : IHomeRowProvider
{
    private readonly IUserViewManager _userViewManager;
    private readonly IHomeRowItemAdapter _adapter;
    private readonly HomeRowCardShape _shape;

    private LibraryTilesProvider(string id, string name, HomeRowCardShape shape, IUserViewManager userViewManager, IHomeRowItemAdapter adapter)
    {
        Id = id;
        Name = name;
        _shape = shape;
        _userViewManager = userViewManager;
        _adapter = adapter;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Description => "Your libraries.";

    /// <summary>
    /// Creates the tiles and buttons providers.
    /// </summary>
    /// <param name="userViewManager">The user view manager.</param>
    /// <param name="adapter">The item adapter.</param>
    /// <returns>The providers.</returns>
    public static IEnumerable<LibraryTilesProvider> CreateAll(IUserViewManager userViewManager, IHomeRowItemAdapter adapter)
    {
        yield return new LibraryTilesProvider(HomeRowProviderIds.LibraryTiles, "My Media", HomeRowCardShape.Thumb, userViewManager, adapter);
        yield return new LibraryTilesProvider(HomeRowProviderIds.LibraryButtons, "My Media (small)", HomeRowCardShape.Button, userViewManager, adapter);
    }

    /// <inheritdoc />
    public Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
        => Task.FromResult(new HomeRowSchema([]));

    /// <inheritdoc />
    public Task<HomeRowResult> GetItemsAsync(HomeRowContext context, JsonObject config, HomeRowPaging paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var folders = _userViewManager.GetUserViews(new UserViewQuery { User = context.User });

        // Fork copy of Jellyfin.Api/Controllers/UserViewsController.cs at v12.2 (GetUserViews). The options are
        // copied because the context's options are shared between rows.
        var options = new DtoOptions
        {
            Fields = [.. context.DtoOptions.Fields, ItemFields.PrimaryImageAspectRatio, ItemFields.DisplayPreferencesId],
            ImageTypes = context.DtoOptions.ImageTypes,
            ImageTypeLimit = context.DtoOptions.ImageTypeLimit,
            EnableImages = context.DtoOptions.EnableImages,
            AddProgramRecordingInfo = context.DtoOptions.AddProgramRecordingInfo,
            EnableUserData = context.DtoOptions.EnableUserData,
            AddCurrentProgram = context.DtoOptions.AddCurrentProgram
        };

        var dtos = _adapter.ToDtos(folders.Cast<MediaBrowser.Controller.Entities.BaseItem>().ToArray(), context with { DtoOptions = options }, skipVisibilityCheck: true);
        return Task.FromResult(new HomeRowResult(dtos, dtos.Count, new HomeRowPresentation(_shape, ImageType.Primary)));
    }
}
