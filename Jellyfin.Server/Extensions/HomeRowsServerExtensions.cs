using System.Text.Json.Nodes;
using Jellyfin.HomeRows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Jellyfin.Server.Extensions;

/// <summary>
/// Registers the home rows feature in the server.
/// </summary>
public static class HomeRowsServerExtensions
{
    /// <summary>
    /// Adds the home rows services, and describes <see cref="JsonObject"/> to Swagger as a free-form object.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddHomeRowsServer(this IServiceCollection services)
    {
        services.AddHomeRows();

        // Without this mapping Swashbuckle describes JsonObject as a map of JsonNode, with its Parent and Root properties.
        services.ConfigureSwaggerGen(options => options.MapType<JsonObject>(() => new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = true
        }));

        return services;
    }
}
