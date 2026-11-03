using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.HomeRows.Providers;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class CardStyleSettingTests
{
    [Theory]
    [InlineData("poster", HomeRowCardShape.Poster, ImageType.Primary, null)]
    [InlineData("thumb", HomeRowCardShape.Thumb, ImageType.Thumb, ImageType.Primary)]
    [InlineData("square", HomeRowCardShape.Square, ImageType.Primary, null)]
    public void CardStyle_MapsToPresentation(string value, HomeRowCardShape shape, ImageType preferred, ImageType? fallback)
    {
        var presentation = CardStyleSetting.ToPresentation(new JsonObject { ["cardStyle"] = value }, "thumb");

        Assert.Equal(new HomeRowPresentation(shape, preferred, fallback), presentation);
    }

    [Fact]
    public void CardStyle_MissingKey_UsesDefault()
    {
        var presentation = CardStyleSetting.ToPresentation(new JsonObject(), "square");

        Assert.Equal(HomeRowCardShape.Square, presentation.CardShape);
    }

    [Fact]
    public void Create_HasThreeOptionsAndDefault()
    {
        var setting = CardStyleSetting.Create("poster");

        Assert.Equal("cardStyle", setting.Key);
        Assert.Equal(HomeRowSettingType.Enum, setting.Type);
        Assert.Equal(["poster", "thumb", "square"], setting.Options.Select(o => o.Value));
        Assert.Equal(["Poster", "Wide", "Square"], setting.Options.Select(o => o.Label));
        Assert.Equal("poster", setting.Default!.GetValue<string>());
    }
}
