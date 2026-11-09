using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// Fork: at startup, adds the Saved row to the stored layouts once
/// (<see cref="IHomeRowConfigurationService.InsertUserListRowsOnceAsync"/>).
/// </summary>
public sealed class UserListRowInsertHost : IHostedService
{
    private readonly IHomeRowConfigurationService _configuration;
    private readonly ILogger<UserListRowInsertHost> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserListRowInsertHost"/> class.
    /// </summary>
    /// <param name="configuration">The home row configuration service.</param>
    /// <param name="logger">The logger.</param>
    public UserListRowInsertHost(IHomeRowConfigurationService configuration, ILogger<UserListRowInsertHost> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await _configuration.InsertUserListRowsOnceAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Added the Saved row to the stored home row layouts");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not fatal: no marker is written, so the next start tries again.
            _logger.LogError(ex, "Adding the Saved row to the stored home row layouts failed");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
