using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: a request to download a discovery film. There is one per film, whoever asked; the discovery app carries it
/// out (Radarr) and reports progress, and the server marks it done once the film is in the library.
/// </summary>
/// <param name="TmdbId">The TMDB id of the film.</param>
/// <param name="State">The state: <see cref="StateRequested"/>, <see cref="StateDownloading"/>, <see cref="StateFailed"/> or <see cref="StateDone"/>.</param>
/// <param name="UpdatedAt">When the request last changed (UTC).</param>
/// <param name="UserIds">The users who asked for the film.</param>
public record DiscoveryRequest(int TmdbId, string State, DateTime UpdatedAt, IReadOnlyList<Guid> UserIds)
{
    /// <summary>
    /// The state of a request the app hasn't picked up yet.
    /// </summary>
    public const string StateRequested = "requested";

    /// <summary>
    /// The state of a request the app has handed to Radarr.
    /// </summary>
    public const string StateDownloading = "downloading";

    /// <summary>
    /// The state of a request whose last attempt failed; the film can be requested again.
    /// </summary>
    public const string StateFailed = "failed";

    /// <summary>
    /// The state of a request whose film is in the library.
    /// </summary>
    public const string StateDone = "done";

    /// <summary>
    /// Gets a value indicating whether the request is still active (not done): its film is kept and protected.
    /// </summary>
    public bool IsActive => !string.Equals(State, StateDone, StringComparison.Ordinal);

    /// <summary>
    /// Gets whether a state is one of the four request states.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>True for a known state.</returns>
    public static bool IsState(string? state)
        => state is StateRequested or StateDownloading or StateFailed or StateDone;
}
