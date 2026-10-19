namespace Jellyfin.HomeRows.Schema;

/// <summary>
/// The type of a home row setting.
/// </summary>
public enum HomeRowSettingType
{
    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>An integer.</summary>
    Integer,

    /// <summary>A string.</summary>
    String,

    /// <summary>One value of a fixed list.</summary>
    Enum,

    /// <summary>Several values of a fixed list.</summary>
    MultiEnum
}
