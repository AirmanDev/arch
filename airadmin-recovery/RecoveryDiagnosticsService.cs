using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AirAdmin.Recovery;

public sealed class RecoveryDiagnosticsService : IHostedService
{
    private readonly RecoveryExecutor _executor;
    private readonly ILogger<RecoveryDiagnosticsService> _logger;

    public RecoveryDiagnosticsService(
        RecoveryExecutor executor,
        ILogger<RecoveryDiagnosticsService> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var state = await _executor.GetAirAdminStateAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = await _executor.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "AIRADMIN-RECOVERY: v1.1 ready; state={State}; diagnostics={Diagnostics}",
            state,
            diagnostics);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
