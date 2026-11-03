using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace Jellyfin.HomeRows.Schema;

/// <summary>
/// Writes a <see cref="HomeRowSchema"/> as a subset of JSON Schema.
/// </summary>
public static class HomeRowSchemaWriter
{
    /// <summary>
    /// Converts the schema to a JSON Schema object.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <returns>The JSON Schema.</returns>
    public static JsonObject ToJsonSchema(HomeRowSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var setting in schema.Settings)
        {
            properties[setting.Key] = WriteSetting(setting);
            if (setting.Default is null)
            {
                required.Add(setting.Key);
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
    }

    /// <summary>
    /// Gets an object holding the default of every setting that has one.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <returns>The defaults.</returns>
    public static JsonObject GetDefaults(HomeRowSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var defaults = new JsonObject();
        foreach (var setting in schema.Settings)
        {
            if (setting.Default is not null)
            {
                defaults[setting.Key] = setting.Default.DeepClone();
            }
        }

        return defaults;
    }

    private static JsonObject WriteSetting(HomeRowSetting setting)
    {
        var node = new JsonObject
        {
            ["type"] = setting.Type switch
            {
                HomeRowSettingType.Boolean => "boolean",
                HomeRowSettingType.Integer => "integer",
                HomeRowSettingType.MultiEnum => "array",
                _ => "string"
            },
            ["title"] = setting.Title
        };

        if (setting.Description is not null)
        {
            node["description"] = setting.Description;
        }

        switch (setting.Type)
        {
            case HomeRowSettingType.Integer:
                if (setting.Minimum is not null)
                {
                    node["minimum"] = setting.Minimum;
                }

                if (setting.Maximum is not null)
                {
                    node["maximum"] = setting.Maximum;
                }

                break;
            case HomeRowSettingType.String:
                node["maxLength"] = HomeRowConfigValidator.MaxStringLength;
                break;
            case HomeRowSettingType.Enum:
                node["enum"] = Values(setting);
                node["x-enumLabels"] = Labels(setting);
                break;
            case HomeRowSettingType.MultiEnum:
                node["items"] = new JsonObject { ["type"] = "string", ["enum"] = Values(setting) };
                node["uniqueItems"] = true;
                if (setting.MinItems is not null)
                {
                    node["minItems"] = setting.MinItems;
                }

                node["x-enumLabels"] = Labels(setting);
                break;
        }

        if (setting.Default is not null)
        {
            node["default"] = setting.Default.DeepClone();
        }

        if (setting.UiHint is not null)
        {
            node["x-ui"] = setting.UiHint;
        }

        return node;
    }

    private static JsonArray Values(HomeRowSetting setting)
        => new(setting.Options.Select(o => (JsonNode?)JsonValue.Create(o.Value)).ToArray());

    private static JsonArray Labels(HomeRowSetting setting)
        => new(setting.Options.Select(o => (JsonNode?)JsonValue.Create(o.Label)).ToArray());
}
