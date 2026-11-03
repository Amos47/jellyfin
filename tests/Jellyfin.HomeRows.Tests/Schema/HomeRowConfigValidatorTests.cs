using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.HomeRows.Schema;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Schema;

public class HomeRowConfigValidatorTests
{
    private static readonly HomeRowSchema _schema = new(
    [
        new HomeRowSetting("count", HomeRowSettingType.Integer, "Count") { Minimum = 1, Maximum = 50, Default = JsonValue.Create(20) },
        new HomeRowSetting("flag", HomeRowSettingType.Boolean, "Flag") { Default = JsonValue.Create(true) },
        new HomeRowSetting("kind", HomeRowSettingType.Enum, "Kind")
        {
            Options = [new HomeRowOption("Movie", "Movies"), new HomeRowOption("Series", "Shows")],
            Default = JsonValue.Create("Movie")
        },
        new HomeRowSetting("kinds", HomeRowSettingType.MultiEnum, "Kinds")
        {
            Options = [new HomeRowOption("a", "A"), new HomeRowOption("b", "B")],
            Default = new JsonArray("a")
        },
        new HomeRowSetting("name", HomeRowSettingType.String, "Name")
    ]);

    private static HomeRowValidationResult Validate(string json) => HomeRowConfigValidator.Validate(_schema, (JsonObject)JsonNode.Parse(json)!);

    [Fact]
    public void Validate_FillsMissingFromDefaults()
    {
        var result = Validate("""{"name":"x"}""");

        Assert.Empty(result.Errors);
        Assert.Equal(20, (int)result.Config["count"]!);
        Assert.True((bool)result.Config["flag"]!);
        Assert.Equal("Movie", (string?)result.Config["kind"]);
        Assert.Equal("""["a"]""", result.Config["kinds"]!.ToJsonString());
        Assert.Equal("x", (string?)result.Config["name"]);
    }

    [Fact]
    public void Validate_DropsUnknownKeys()
    {
        var result = Validate("""{"name":"x","bogus":1}""");

        Assert.Empty(result.Errors);
        Assert.False(result.Config.ContainsKey("bogus"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    [InlineData("1.5")]
    [InlineData("2.0")]
    public void Validate_IntegerOutOfRangeOrNotInteger_ErrorAndDefault(string value)
    {
        var result = Validate($$"""{"name":"x","count":{{value}}}""");

        Assert.Equal("count", Assert.Single(result.Errors).Key);
        Assert.Equal(20, (int)result.Config["count"]!);
    }

    [Fact]
    public void Validate_IntegerInRange_Accepted()
    {
        var result = Validate("""{"name":"x","count":7}""");

        Assert.Empty(result.Errors);
        Assert.Equal(7, (int)result.Config["count"]!);
    }

    [Fact]
    public void Validate_EnumNotInOptions_ErrorAndDefault()
    {
        var result = Validate("""{"name":"x","kind":"Book"}""");

        Assert.Equal("kind", Assert.Single(result.Errors).Key);
        Assert.Equal("Movie", (string?)result.Config["kind"]);
    }

    [Theory]
    [InlineData("""["a","a"]""")]
    [InlineData("""["a","zzz"]""")]
    [InlineData("""[1]""")]
    public void Validate_MultiEnumDuplicatesOrUnknown_Error(string value)
    {
        var result = Validate($$"""{"name":"x","kinds":{{value}}}""");

        Assert.Equal("kinds", Assert.Single(result.Errors).Key);
        Assert.Equal("""["a"]""", result.Config["kinds"]!.ToJsonString());
    }

    [Fact]
    public void Validate_MultiEnumValid_Accepted()
    {
        var result = Validate("""{"name":"x","kinds":["b","a"]}""");

        Assert.Empty(result.Errors);
        Assert.Equal("""["b","a"]""", result.Config["kinds"]!.ToJsonString());
    }

    [Theory]
    [InlineData("flag", "\"yes\"")]
    [InlineData("count", "\"5\"")]
    [InlineData("count", "null")]
    [InlineData("name", "5")]
    [InlineData("kind", "[]")]
    public void Validate_WrongJsonType_Error(string key, string value)
    {
        var input = new JsonObject { ["name"] = "x" };
        input[key] = JsonNode.Parse(value);
        var result = HomeRowConfigValidator.Validate(_schema, input);

        Assert.Contains(result.Errors, e => e.Key == key);
    }

    [Fact]
    public void Validate_RequiredMissing_ErrorAndKeyAbsent()
    {
        var result = Validate("{}");

        Assert.Equal("name", Assert.Single(result.Errors).Key);
        Assert.False(result.Config.ContainsKey("name"));
    }

    [Fact]
    public void Validate_StringTooLong_Error()
    {
        var result = Validate($$"""{"name":"{{new string('a', 201)}}"}""");

        Assert.Equal("name", Assert.Single(result.Errors).Key);
        Assert.False(result.Config.ContainsKey("name"));
    }

    [Fact]
    public void Validate_StringAtLimit_Accepted()
    {
        var result = Validate($$"""{"name":"{{new string('a', 200)}}"}""");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_NullInput_TreatedAsEmpty()
    {
        var result = HomeRowConfigValidator.Validate(_schema, null);

        Assert.Equal("name", Assert.Single(result.Errors).Key);
        Assert.Equal(20, (int)result.Config["count"]!);
    }

    [Fact]
    public void Validate_DoesNotAliasDefaults()
    {
        var first = Validate("""{"name":"x"}""");
        ((JsonArray)first.Config["kinds"]!).Add("b");

        var second = Validate("""{"name":"x"}""");

        Assert.Equal("""["a"]""", second.Config["kinds"]!.ToJsonString());
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("""["a"]""", true)]
    public void Validator_MinItems(string value, bool valid)
    {
        var schema = new HomeRowSchema(
        [
            new HomeRowSetting("kinds", HomeRowSettingType.MultiEnum, "Kinds")
            {
                Options = [new HomeRowOption("a", "A"), new HomeRowOption("b", "B")],
                Default = new JsonArray("a", "b"),
                MinItems = 1
            }
        ]);

        var result = HomeRowConfigValidator.Validate(schema, (JsonObject)JsonNode.Parse($$"""{"kinds":{{value}}}""")!);

        if (valid)
        {
            Assert.Empty(result.Errors);
            Assert.Equal(value, result.Config["kinds"]!.ToJsonString());
        }
        else
        {
            Assert.Equal("kinds", Assert.Single(result.Errors).Key);
            Assert.Equal("""["a","b"]""", result.Config["kinds"]!.ToJsonString());
        }
    }
}
