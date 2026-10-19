using System.Collections.Generic;
using System.Text.Json.Nodes;
using Jellyfin.HomeRows.Schema;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Schema;

public class HomeRowSchemaWriterTests
{
    [Fact]
    public void ToJsonSchema_EmitsSupportedSubset()
    {
        var schema = new HomeRowSchema(
        [
            new HomeRowSetting("flag", HomeRowSettingType.Boolean, "Flag") { Default = JsonValue.Create(true) },
            new HomeRowSetting("count", HomeRowSettingType.Integer, "Count") { Minimum = 1, Maximum = 50, Default = JsonValue.Create(20) },
            new HomeRowSetting("text", HomeRowSettingType.String, "Text"),
            new HomeRowSetting("kind", HomeRowSettingType.Enum, "Kind")
            {
                Options = [new HomeRowOption("Movie", "Movies"), new HomeRowOption("Series", "Shows")],
                UiHint = "radio"
            },
            new HomeRowSetting("kinds", HomeRowSettingType.MultiEnum, "Kinds")
            {
                Description = "Pick some",
                Options = [new HomeRowOption("a", "A"), new HomeRowOption("b", "B")]
            }
        ]);

        var json = HomeRowSchemaWriter.ToJsonSchema(schema);

        var expected = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "flag": { "type": "boolean", "title": "Flag", "default": true },
            "count": { "type": "integer", "title": "Count", "minimum": 1, "maximum": 50, "default": 20 },
            "text": { "type": "string", "title": "Text", "maxLength": 200 },
            "kind": { "type": "string", "title": "Kind", "enum": ["Movie", "Series"], "x-enumLabels": ["Movies", "Shows"], "x-ui": "radio" },
            "kinds": { "type": "array", "title": "Kinds", "description": "Pick some", "items": { "type": "string", "enum": ["a", "b"] }, "uniqueItems": true, "x-enumLabels": ["A", "B"] }
          },
          "required": ["text", "kind", "kinds"]
        }
        """);
        Assert.True(JsonNode.DeepEquals(expected, json), json.ToJsonString());
    }

    [Fact]
    public void GetDefaults_ReturnsOnlySettingsWithDefaults()
    {
        var schema = new HomeRowSchema(
        [
            new HomeRowSetting("a", HomeRowSettingType.Integer, "A") { Default = JsonValue.Create(5) },
            new HomeRowSetting("b", HomeRowSettingType.String, "B")
        ]);

        var defaults = HomeRowSchemaWriter.GetDefaults(schema);

        Assert.Equal("""{"a":5}""", defaults.ToJsonString());
    }
}
