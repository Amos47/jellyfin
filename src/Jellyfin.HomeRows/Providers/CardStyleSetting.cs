using System;
using System.Text.Json.Nodes;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Model.Entities;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// The shared card style setting of the standard home rows.
/// </summary>
public static class CardStyleSetting
{
    /// <summary>
    /// The setting key.
    /// </summary>
    public const string Key = "cardStyle";

    /// <summary>
    /// Creates the setting.
    /// </summary>
    /// <param name="defaultValue">The default option value.</param>
    /// <returns>The setting.</returns>
    public static HomeRowSetting Create(string defaultValue)
    {
        return new HomeRowSetting(Key, HomeRowSettingType.Enum, "Card style")
        {
            Default = JsonValue.Create(defaultValue),
            Options =
            [
                new HomeRowOption("poster", "Poster"),
                new HomeRowOption("thumb", "Wide"),
                new HomeRowOption("square", "Square")
            ]
        };
    }

    /// <summary>
    /// Maps the configured card style to a presentation.
    /// </summary>
    /// <param name="config">The row configuration.</param>
    /// <param name="defaultValue">The value used when the key is missing or invalid.</param>
    /// <returns>The presentation.</returns>
    public static HomeRowPresentation ToPresentation(JsonObject config, string defaultValue)
    {
        ArgumentNullException.ThrowIfNull(config);

        var value = config[Key] is JsonValue node && node.TryGetValue<string>(out var s) ? s : defaultValue;
        return Map(value) ?? Map(defaultValue) ?? Map("thumb")!;
    }

    private static HomeRowPresentation? Map(string value) => value switch
    {
        "poster" => new HomeRowPresentation(HomeRowCardShape.Poster, ImageType.Primary),
        "thumb" => new HomeRowPresentation(HomeRowCardShape.Thumb, ImageType.Thumb, ImageType.Primary),
        "square" => new HomeRowPresentation(HomeRowCardShape.Square, ImageType.Primary),
        _ => null
    };
}
