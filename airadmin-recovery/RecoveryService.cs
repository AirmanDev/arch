using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AirAdmin.Recovery;

public sealed class RecoveryService : IHostedService
{
    private readonly ILogger<RecoveryService> _logger;

    public RecoveryService(ILogger<RecoveryService> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning("AIRADMIN-RECOVERY: recovery helper started");

        var id = await RunAsync("/usr/bin/id", Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("AIRADMIN-RECOVERY: process identity exit={ExitCode}; stdout={Stdout}; stderr={Stderr}", id.ExitCode, Clean(id.Stdout), Clean(id.Stderr));

        var before = await RunSystemctlAsync(new[] { "is-active", "airadmin.service" }, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("AIRADMIN-RECOVERY: initial airadmin state exit={ExitCode}; stdout={Stdout}; stderr={Stderr}", before.ExitCode, Clean(before.Stdout), Clean(before.Stderr));

        if (before.ExitCode == 0 && before.Stdout.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("AIRADMIN-RECOVERY: RESULT=ALREADY_ACTIVE");
            return;
        }

        var sudo = File.Exists("/usr/bin/sudo") ? "/usr/bin/sudo" : "/bin/sudo";
        var systemctl = FindSystemctl();

        if (File.Exists(sudo) && systemctl is not null)
        {
            var sudoStart = await RunAsync(sudo, new[] { "-n", systemctl, "start", "airadmin.service" }, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("AIRADMIN-RECOVERY: sudo start exit={ExitCode}; stdout={Stdout}; stderr={Stderr}", sudoStart.ExitCode, Clean(sudoStart.Stdout), Clean(sudoStart.Stderr));
        }
        else
        {
            _logger.LogWarning("AIRADMIN-RECOVERY: sudo or systemctl executable not found");
        }

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        var afterSudo = await RunSystemctlAsync(new[] { "is-active", "airadmin.service" }, cancellationToken).ConfigureAwait(false);
        if (afterSudo.ExitCode == 0 && afterSudo.Stdout.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("AIRADMIN-RECOVERY: RESULT=STARTED_BY_SUDO");
            return;
        }

        if (systemctl is not null)
        {
            var directStart = await RunAsync(systemctl, new[] { "start", "airadmin.service" }, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("AIRADMIN-RECOVERY: direct start exit={ExitCode}; stdout={Stdout}; stderr={Stderr}", directStart.ExitCode, Clean(directStart.Stdout), Clean(directStart.Stderr));
        }

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        var final = await RunSystemctlAsync(new[] { "is-active", "airadmin.service" }, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("AIRADMIN-RECOVERY: final airadmin state exit={ExitCode}; stdout={Stdout}; stderr={Stderr}", final.ExitCode, Clean(final.Stdout), Clean(final.Stderr));

        if (final.ExitCode == 0 && final.Stdout.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("AIRADMIN-RECOVERY: RESULT=STARTED");
        }
        else
        {
            var sudoList = File.Exists(sudo)
                ? await RunAsync(sudo, new[] { "-n", "-l" }, cancellationToken).ConfigureAwait(false)
                : new CommandResult(127, string.Empty, "sudo not found");
            _logger.LogWarning("AIRADMIN-RECOVERY: sudo -n -l exit={ExitCode}; stdout={Stdout}; stderr={Stderr}", sudoList.ExitCode, Clean(sudoList.Stdout), Clean(sudoList.Stderr));
            _logger.LogError("AIRADMIN-RECOVERY: RESULT=FAILED. Open this Jellyfin log and provide the AIRADMIN-RECOVERY lines for diagnosis.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<CommandResult> RunSystemctlAsync(string[] args, CancellationToken cancellationToken)
    {
        var systemctl = FindSystemctl();
        return systemctl is null
            ? new CommandResult(127, string.Empty, "systemctl not found")
            : await RunAsync(systemctl, args, cancellationToken).ConfigureAwait(false);
    }

    private static string? FindSystemctl()
    {
        if (File.Exists("/usr/bin/systemctl")) return "/usr/bin/systemctl";
        if (File.Exists("/bin/systemctl")) return "/bin/systemctl";
        return null;
    }

    private async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new CommandResult(126, string.Empty, "process failed to start");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new CommandResult(124, await stdoutTask.ConfigureAwait(false), "command timed out");
            }

            return new CommandResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AIRADMIN-RECOVERY: command execution error for {Executable}", executable);
            return new CommandResult(125, string.Empty, ex.Message);
        }
    }

    private static string Clean(string value)
    {
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 2000 ? text : text[..2000] + "...[truncated]";
    }

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
}
