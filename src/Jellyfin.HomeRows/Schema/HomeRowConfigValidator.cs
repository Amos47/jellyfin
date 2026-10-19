using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.HomeRows.Schema;

/// <summary>
/// Validates row configuration against a <see cref="HomeRowSchema"/>.
/// </summary>
public static class HomeRowConfigValidator
{
    /// <summary>
    /// The maximum length of a string setting.
    /// </summary>
    public const int MaxStringLength = 200;

    /// <summary>
    /// Validates the input. Unknown keys are dropped and missing keys take their default. Callers saving
    /// configuration reject it when <see cref="HomeRowValidationResult.Errors"/> is not empty, callers
    /// reading it use <see cref="HomeRowValidationResult.Config"/>.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <param name="input">The input, null is treated as empty.</param>
    /// <returns>The validation result.</returns>
    public static HomeRowValidationResult Validate(HomeRowSchema schema, JsonObject? input)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var config = new JsonObject();
        var errors = new List<HomeRowValidationError>();
        foreach (var setting in schema.Settings)
        {
            JsonNode? value = null;
            string? error = null;
            if (input is not null && input.TryGetPropertyValue(setting.Key, out var provided))
            {
                error = Check(setting, provided, out value);
            }
            else if (setting.Default is null)
            {
                error = "A value is required.";
            }
            else
            {
                value = setting.Default;
            }

            if (error is not null)
            {
                errors.Add(new HomeRowValidationError(setting.Key, error));
                value = setting.Default;
            }

            if (value is not null)
            {
                config[setting.Key] = value.DeepClone();
            }
        }

        return new HomeRowValidationResult(config, errors);
    }

    private static string? Check(HomeRowSetting setting, JsonNode? node, out JsonNode? value)
    {
        value = node;
        switch (setting.Type)
        {
            case HomeRowSettingType.Boolean:
                return node is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                    ? null
                    : "Expected a boolean.";
            case HomeRowSettingType.Integer:
                if (node is not JsonValue n || n.GetValueKind() != JsonValueKind.Number || !n.TryGetValue<int>(out var number))
                {
                    return "Expected an integer.";
                }

                if (setting.Minimum is { } min && number < min)
                {
                    return $"Must be at least {min}.";
                }

                return setting.Maximum is { } max && number > max ? $"Must be at most {max}." : null;
            case HomeRowSettingType.String:
                if (node is not JsonValue s || !s.TryGetValue<string>(out var text))
                {
                    return "Expected a string.";
                }

                return text.Length > MaxStringLength ? $"Must be at most {MaxStringLength} characters." : null;
            case HomeRowSettingType.Enum:
                if (node is not JsonValue e || !e.TryGetValue<string>(out var option))
                {
                    return "Expected a string.";
                }

                return setting.Options.Any(o => o.Value == option) ? null : "Not one of the allowed values.";
            case HomeRowSettingType.MultiEnum:
                if (node is not JsonArray array)
                {
                    return "Expected an array.";
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in array)
                {
                    if (item is not JsonValue v || !v.TryGetValue<string>(out var entry))
                    {
                        return "Expected an array of strings.";
                    }

                    if (!setting.Options.Any(o => o.Value == entry))
                    {
                        return $"'{entry}' is not one of the allowed values.";
                    }

                    if (!seen.Add(entry))
                    {
                        return $"'{entry}' is duplicated.";
                    }
                }

                return null;
            default:
                return "Unsupported setting type.";
        }
    }
}
