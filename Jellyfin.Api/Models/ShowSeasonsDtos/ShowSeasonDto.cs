using System.Collections.Generic;

namespace Jellyfin.Api.Models.ShowSeasonsDtos;

/// <summary>
/// One season on the show page.
/// </summary>
/// <param name="SeasonNumber">The season number (0 for specials).</param>
/// <param name="Name">The name.</param>
/// <param name="State">The state: notOnServer, requested, downloading, onServer or partly.</param>
/// <param name="OnServer">How many of its episodes are on the server.</param>
/// <param name="Aired">How many of its episodes have aired.</param>
/// <param name="Total">How many episodes it has.</param>
/// <param name="CanGet">Whether "Get season" is offered.</param>
/// <param name="Episodes">The episodes, by episode number.</param>
public sealed record ShowSeasonDto(
    int SeasonNumber,
    string Name,
    string State,
    int OnServer,
    int Aired,
    int Total,
    bool CanGet,
    IReadOnlyList<ShowEpisodeDto> Episodes);
