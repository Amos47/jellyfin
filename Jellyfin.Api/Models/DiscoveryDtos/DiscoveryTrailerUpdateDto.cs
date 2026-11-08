namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A trailer file to attach to a discovery film.
/// </summary>
/// <param name="Path">The full path of the file, inside the configured trailer folder.</param>
public sealed record DiscoveryTrailerUpdateDto(string? Path);
