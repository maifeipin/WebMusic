using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class CredentialsController : ControllerBase
{
    private readonly AppDbContext _context;

    public CredentialsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<StorageCredential>>> GetCredentials()
    {
        return await _context.StorageCredentials.ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<StorageCredential>> AddCredential(StorageCredential credential)
    {
        _context.StorageCredentials.Add(credential);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetCredentials), new { id = credential.Id }, credential);
    }
    
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCredential(int id)
    {
        var cred = await _context.StorageCredentials.FindAsync(id);
        if (cred == null) return NotFound();
        
        _context.StorageCredentials.Remove(cred);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("test")]
    public IActionResult TestConnection([FromBody] StorageCredential credential, [FromServices] WebMusic.Backend.Services.ISmbService smbService)
    {
        // Construct a temporary Source to test connection
        var tempSource = new ScanSource 
        { 
            Path = "smb://" + credential.Host + "/test", // Dummy share, we just want to test Login
            StorageCredential = credential 
        };

        // We can't easily test just login with current SmbService interface which does Connect+Login+TreeConnect all in one.
        // We really just want to test "Connect + Login".
        // For MVP, we'll try to Connect. If it fails on TreeConnect it's fine, as long as Login worked.
        // But SmbService.Connect returns false if any step fails.
        // Let's rely on SmbService.Connect but be aware it might fail if "test" share doesn't exist.
        // Ideally we refactor SmbService to separate Login check.
        // For now, let's assume if it returns false it failed.
        
        // Let's create a specific Test method in SmbService later. 
        // For now, let's use the Connect method and expect it might fail on TreeConnect.
        // Actually, user wants to test CREDENTIALS validity.
        
        // Let's add a Test method to ISmbService.
        return Ok(new { success = smbService.TestCredentials(credential) });
    }
}
