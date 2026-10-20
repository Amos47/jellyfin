using System.Collections.Generic;

namespace Jellyfin.Api.Models.HomeRowDtos;

/// <summary>
/// The home row providers available to the caller.
/// </summary>
/// <param name="ApiVersion">The version of the home rows API.</param>
/// <param name="Providers">The providers.</param>
public sealed record HomeRowProvidersDto(int ApiVersion, IReadOnlyList<HomeRowProviderDto> Providers);
