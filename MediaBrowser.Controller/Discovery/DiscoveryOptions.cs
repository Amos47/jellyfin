namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: the settings of the movie discovery hub, stored as the named configuration "discovery".
/// </summary>
public class DiscoveryOptions
{
    /// <summary>
    /// Gets or sets the folder pushed trailer files must be in. No trailers are accepted while it is not set.
    /// </summary>
    public string? TrailerRoot { get; set; }
}
