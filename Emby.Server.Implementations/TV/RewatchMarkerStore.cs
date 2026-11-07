using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.TV;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.TV;

/// <summary>
/// Fork: keeps rewatch markers in <c>rewatch-markers.json</c> in the data folder, outside jellyfin.db, so the
/// database schema stays the same as upstream's.
/// </summary>
public sealed class RewatchMarkerStore : IRewatchMarkerStore
{
    private const string FileName = "rewatch-markers.json";

    private readonly string _path;
    private readonly ILogger<RewatchMarkerStore> _logger;
    private readonly Lock _lock = new();
    private Dictionary<Guid, List<RewatchMarker>>? _markers;

    /// <summary>
    /// Initializes a new instance of the <see cref="RewatchMarkerStore"/> class.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public RewatchMarkerStore(IApplicationPaths paths, ILogger<RewatchMarkerStore> logger)
    {
        _path = Path.Combine(paths.DataPath, FileName);
        _logger = logger;
    }

    /// <inheritdoc />
    public RewatchMarker? Get(Guid userId, string seriesKey)
    {
        lock (_lock)
        {
            return Load().GetValueOrDefault(userId)?.FirstOrDefault(m => m.SeriesKey == seriesKey);
        }
    }

    /// <inheritdoc />
    public void Set(Guid userId, RewatchMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        lock (_lock)
        {
            var markers = Load();
            if (!markers.TryGetValue(userId, out var list))
            {
                list = [];
                markers[userId] = list;
            }

            list.RemoveAll(m => m.SeriesKey == marker.SeriesKey);
            list.Add(marker);
            Save(markers);
        }
    }

    /// <inheritdoc />
    public void Remove(Guid userId, string seriesKey)
    {
        lock (_lock)
        {
            var markers = Load();
            if (markers.TryGetValue(userId, out var list) && list.RemoveAll(m => m.SeriesKey == seriesKey) > 0)
            {
                Save(markers);
            }
        }
    }

    private Dictionary<Guid, List<RewatchMarker>> Load()
    {
        if (_markers is not null)
        {
            return _markers;
        }

        _markers = [];
        if (File.Exists(_path))
        {
            try
            {
                using var stream = File.OpenRead(_path);
                _markers = JsonSerializer.Deserialize<Dictionary<Guid, List<RewatchMarker>>>(stream, JsonDefaults.Options) ?? [];
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Ignoring unreadable rewatch markers in {Path}", _path);
            }
        }

        return _markers;
    }

    private void Save(Dictionary<Guid, List<RewatchMarker>> markers)
    {
        var temp = _path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, markers, JsonDefaults.Options);
        }

        File.Move(temp, _path, true);
    }
}
