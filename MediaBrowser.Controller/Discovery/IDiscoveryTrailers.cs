using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: attaches trailer files pushed by outside apps to discovery films, as local trailers of the film.
/// </summary>
public interface IDiscoveryTrailers
{
    /// <summary>
    /// Makes a file the trailer of a discovery film, replacing any trailer it had. The file must be inside the
    /// configured <see cref="DiscoveryOptions.TrailerRoot"/> once every symlink is resolved. The file itself is never
    /// changed.
    /// </summary>
    /// <param name="tmdbId">The TMDB id of the film.</param>
    /// <param name="path">The full path of the trailer file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<DiscoveryTrailerResult> SetAsync(int tmdbId, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the trailer items of a discovery film. Their files are left on disk.
    /// </summary>
    /// <param name="tmdbId">The TMDB id of the film.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task RemoveAsync(int tmdbId, CancellationToken cancellationToken);
}
