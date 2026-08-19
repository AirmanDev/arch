using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AirAdmin.Recovery;

public sealed class RecoveryExecutor
{
    private static readonly Regex SafeUsername =
        new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<RecoveryExecutor> _logger;

    public RecoveryExecutor(ILogger<RecoveryExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<RecoveryResult> StartAirAdminAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (!SafeUsername.IsMatch(username))
        {
            return new RecoveryResult(
                false,
                "Érvénytelen Linux felhasználónév.",
                await GetAirAdminStateAsync(cancellationToken).ConfigureAwait(false),
                "none",
                "Username validation failed.");
        }

        var current = await GetAirAdminStateAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(current, "active", StringComparison.OrdinalIgnoreCase))
        {
            return new RecoveryResult(true, "Az AirAdmin már fut.", current, "none", "Already active.");
        }

        var sshPath = FindExecutable("/usr/bin/ssh", "/bin/ssh");
        var sshReachable = sshPath is not null
            && await IsTcpReachableAsync("127.0.0.1", 22, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

        if (!sshReachable || sshPath is null)
        {
            _logger.LogWarning(
                "AIRADMIN-RECOVERY: localhost SSH unavailable. sshPath={SshPath}; reachable={Reachable}",
                sshPath ?? "(missing)",
                sshReachable);

            return new RecoveryResult(
                false,
                "A helyi SSH (127.0.0.1:22) nem érhető el, ezért ezen az úton nem tudtam jogosultságot váltani.",
                await GetAirAdminStateAsync(cancellationToken).ConfigureAwait(false),
                "localhost-ssh",
                $"sshPath={sshPath ?? "(missing)"}; localhost22={sshReachable}");
        }

        var askPassPath = Path.Combine(
            Path.GetTempPath(),
            $"airadmin-recovery-askpass-{Guid.NewGuid():N}.sh");

        try
        {
            await File.WriteAllTextAsync(
                askPassPath,
                "#!/bin/sh\nprintf '%s\\n' \"$AIRADMIN_RECOVERY_PASSWORD\"\n",
                cancellationToken).ConfigureAwait(false);

            File.SetUnixFileMode(
                askPassPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var psi = new ProcessStartInfo
            {
                FileName = sshPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (var arg in new[]
            {
                "-o", "BatchMode=no",
                "-o", "StrictHostKeyChecking=no",
                "-o", "UserKnownHostsFile=/dev/null",
                "-o", "PreferredAuthentications=password,keyboard-interactive",
                "-o", "PubkeyAuthentication=no",
                "-o", "NumberOfPasswordPrompts=1",
                "-o", "ConnectTimeout=5",
                "-o", "LogLevel=ERROR",
                $"{username}@127.0.0.1",
                "sudo -S -p '' /usr/bin/systemctl start airadmin.service && /usr/bin/systemctl is-active airadmin.service"
            })
            {
                psi.ArgumentList.Add(arg);
            }

            psi.Environment["SSH_ASKPASS"] = askPassPath;
            psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            psi.Environment["DISPLAY"] = "airadmin-recovery:0";
            psi.Environment["AIRADMIN_RECOVERY_PASSWORD"] = password;

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new RecoveryResult(
                    false,
                    "Nem sikerült elindítani a helyi SSH klienst.",
                    await GetAirAdminStateAsync(cancellationToken).ConfigureAwait(false),
                    "localhost-ssh",
                    "ssh process failed to start");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.StandardInput.WriteLineAsync(password).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return new RecoveryResult(
                    false,
                    "A helyi SSH helyreállítás időtúllépés miatt megszakadt.",
                    await GetAirAdminStateAsync(cancellationToken).ConfigureAwait(false),
                    "localhost-ssh",
                    "timeout");
            }

            var stdout = Clean(await stdoutTask.ConfigureAwait(false));
            var stderr = Clean(await stderrTask.ConfigureAwait(false));
            var state = await GetAirAdminStateAsync(cancellationToken).ConfigureAwait(false);
            var success = process.ExitCode == 0
                && string.Equals(state, "active", StringComparison.OrdinalIgnoreCase);

            _logger.LogWarning(
                "AIRADMIN-RECOVERY: localhost SSH result exit={ExitCode}; state={State}; stdout={Stdout}; stderr={Stderr}",
                process.ExitCode,
                state,
                stdout,
                stderr);

            return new RecoveryResult(
                success,
                success
                    ? "Siker: az airadmin.service elindult."
                    : "Az SSH kapcsolat létrejött, de az airadmin.service nem indult el. A részleteket kiírtam.",
                state,
                "localhost-ssh",
                $"exit={process.ExitCode}; stdout={stdout}; stderr={stderr}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AIRADMIN-RECOVERY: localhost SSH recovery failed");

            return new RecoveryResult(
                false,
                "A helyreállítás közben hiba történt.",
                await GetAirAdminStateAsync(cancellationToken).ConfigureAwait(false),
                "localhost-ssh",
                ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(askPassPath))
                {
                    File.Delete(askPassPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AIRADMIN-RECOVERY: could not delete temporary askpass helper");
            }
        }
    }

    public async Task<string> GetAirAdminStateAsync(CancellationToken cancellationToken)
    {
        var systemctl = FindExecutable("/usr/bin/systemctl", "/bin/systemctl");
        if (systemctl is null)
        {
            return "systemctl-not-found";
        }

        var result = await RunAsync(
            systemctl,
            new[] { "is-active", "airadmin.service" },
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        var state = result.Stdout.Trim();
        return string.IsNullOrWhiteSpace(state) ? $"unknown(exit={result.ExitCode})" : state;
    }

    public async Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var systemctl = FindExecutable("/usr/bin/systemctl", "/bin/systemctl");
        var ssh = FindExecutable("/usr/bin/ssh", "/bin/ssh");
        var sshReachable = await IsTcpReachableAsync(
            "127.0.0.1",
            22,
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);

        if (systemctl is null)
        {
            return $"systemctl=(missing); ssh={ssh ?? "(missing)"}; localhost22={sshReachable}";
        }

        var show = await RunAsync(
            systemctl,
            new[]
            {
                "show",
                "airadmin.service",
                "--no-pager",
                "-p", "FragmentPath",
                "-p", "User",
                "-p", "Group",
                "-p", "WorkingDirectory",
                "-p", "ExecStart"
            },
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        return $"ssh={ssh ?? "(missing)"}; localhost22={sshReachable}; systemd={Clean(show.Stdout)}; stderr={Clean(show.Stderr)}";
    }

    private static async Task<bool> IsTcpReachableAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindExecutable(params string[] paths)
    {
        return paths.FirstOrDefault(File.Exists);
    }

    private static async Task<CommandResult> RunAsync(
        string executable,
        IEnumerable<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return new CommandResult(124, await stdoutTask.ConfigureAwait(false), "timeout");
            }

            return new CommandResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
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
