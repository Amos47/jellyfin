using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace Jellyfin.HomeRows;

/// <inheritdoc />
public class HomeRowItemAdapter : IHomeRowItemAdapter
{
    private readonly IDtoService _dtoService;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeRowItemAdapter"/> class.
    /// </summary>
    /// <param name="dtoService">The DTO service.</param>
    public HomeRowItemAdapter(IDtoService dtoService)
    {
        _dtoService = dtoService;
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItemDto> ToDtos(IReadOnlyList<BaseItem> items, HomeRowContext context, bool skipVisibilityCheck = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(context);

        return _dtoService.GetBaseItemDtos(items, context.DtoOptions, context.User, null, skipVisibilityCheck);
    }
}
