using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Jellyfin.HomeRows.Schema;

/// <summary>
/// The result of validating a row configuration.
/// </summary>
/// <param name="Config">The sanitized configuration. Every invalid setting holds its default, or is absent when it has none.</param>
/// <param name="Errors">The validation errors.</param>
public sealed record HomeRowValidationResult(JsonObject Config, IReadOnlyList<HomeRowValidationError> Errors);
