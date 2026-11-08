using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: hidden plugin folder that holds the virtual films of the movie discovery feature.
/// </summary>
[RequiresSourceSerialisation]
public class DiscoveryFolder : BasePluginFolder
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryFolder"/> class.
    /// </summary>
    public DiscoveryFolder()
    {
        Name = "Discovery";
    }

    /// <inheritdoc />
    [JsonIgnore]
    public override bool IsHidden => true;

    /// <inheritdoc />
    [JsonIgnore]
    public override bool SupportsInheritedParentImages => false;

    /// <inheritdoc />
    [JsonIgnore]
    public override CollectionType? CollectionType => null;

    /// <inheritdoc />
    public override string GetClientTypeName()
    {
        // BaseItemKind has no entry for this fork type and the DTO code parses the name into it.
        return nameof(BaseItemKind.Folder);
    }
}
