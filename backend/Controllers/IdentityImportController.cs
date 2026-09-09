using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;

namespace WebMusic.Backend.Controllers;

[ApiController]
[Route("api/identity-import")]
[Authorize(Roles = "Admin")]
public class IdentityImportController : ControllerBase
{
    private readonly IIdentityImportService _importService;
    private readonly ILogger<IdentityImportController> _logger;

    public IdentityImportController(IIdentityImportService importService, ILogger<IdentityImportController> logger)
    {
        _importService = importService;
        _logger = logger;
    }

    private string GetCurrentUsername()
    {
        return User.FindFirst("name")?.Value
               ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Name)?.Value
               ?? User.FindFirst(ClaimTypes.Name)?.Value
               ?? User.Identity?.Name
               ?? "admin";
    }

    [HttpPost("preview")]
    public async Task<ActionResult<IdentityImportPreviewResult>> PreviewBatch([FromBody] PreviewIdentityImportBatchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _importService.PreviewBatchAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to preview identity import batch");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("batches")]
    public async Task<ActionResult<IdentityImportBatch>> CreateDraftBatch([FromBody] CreateIdentityImportBatchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var username = GetCurrentUsername();
            var batch = await _importService.CreateDraftBatchAsync(request, username, cancellationToken);
            return CreatedAtAction(nameof(GetBatch), new { id = batch.Id }, batch);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create identity import draft batch");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("batches/{id}/approve")]
    public async Task<ActionResult<IdentityImportBatch>> ApproveBatch(int id, CancellationToken cancellationToken)
    {
        try
        {
            var username = GetCurrentUsername();
            var batch = await _importService.ApproveBatchAsync(id, username, cancellationToken);
            return Ok(batch);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve identity import batch {Id}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("batches/{id}/apply")]
    public async Task<ActionResult<IdentityImportBatch>> ApplyBatch(int id, CancellationToken cancellationToken)
    {
        try
        {
            var username = GetCurrentUsername();
            var batch = await _importService.ApplyBatchAsync(id, username, cancellationToken);
            return Ok(batch);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply identity import batch {Id}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("batches/{id}/rollback")]
    public async Task<ActionResult<IdentityImportBatch>> RollbackBatch(int id, CancellationToken cancellationToken)
    {
        try
        {
            var username = GetCurrentUsername();
            var batch = await _importService.RollbackBatchAsync(id, username, cancellationToken);
            return Ok(batch);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rollback identity import batch {Id}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("batches/backfill-legacy-pilot")]
    public async Task<ActionResult<IdentityImportBatch>> BackfillLegacyPilot(CancellationToken cancellationToken)
    {
        try
        {
            var username = GetCurrentUsername();
            var batch = await _importService.BackfillLegacyPilotRound2Async(username, cancellationToken);
            return Ok(batch);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backfill legacy pilot batch");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("batches")]
    public async Task<ActionResult<List<IdentityImportBatch>>> GetBatches(CancellationToken cancellationToken)
    {
        var batches = await _importService.GetBatchesAsync(cancellationToken);
        return Ok(batches);
    }

    [HttpGet("batches/{id}")]
    public async Task<ActionResult<IdentityImportBatch>> GetBatch(int id, CancellationToken cancellationToken)
    {
        var batch = await _importService.GetBatchAsync(id, cancellationToken);
        if (batch == null) return NotFound(new { error = $"Batch with ID {id} not found." });
        return Ok(batch);
    }
}
