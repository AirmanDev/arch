using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AirAdmin.Recovery;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("AirAdminRecovery")]
public sealed class RecoveryController : ControllerBase
{
    private readonly RecoveryCryptoService _crypto;
    private readonly RecoveryExecutor _executor;
    private readonly ILogger<RecoveryController> _logger;

    public RecoveryController(
        RecoveryCryptoService crypto,
        RecoveryExecutor executor,
        ILogger<RecoveryController> logger)
    {
        _crypto = crypto;
        _executor = executor;
        _logger = logger;
    }

    [HttpGet("Challenge")]
    public ActionResult<ChallengeResponse> Challenge()
    {
        return Ok(_crypto.CreateChallenge());
    }

    [HttpGet("Status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var state = await _executor.GetAirAdminStateAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = await _executor.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { state, diagnostics });
    }

    [HttpPost("Start")]
    public async Task<ActionResult<RecoveryResult>> Start(
        [FromBody] RecoveryRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var credentials = _crypto.DecryptAndConsume(request.Ciphertext);

            var result = await _executor.StartAirAdminAsync(
                credentials.Username,
                credentials.Password,
                cancellationToken).ConfigureAwait(false);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AIRADMIN-RECOVERY: rejected recovery request");
            return BadRequest(new RecoveryResult(
                false,
                "A titkosított helyreállítási adat érvénytelen vagy lejárt.",
                "unknown",
                "none",
                ex.Message));
        }
    }
}
