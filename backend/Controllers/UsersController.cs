using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = "Admin")] // Whole controller is Admin only
public class UsersController : ControllerBase
{
    private readonly AppDbContext _context;

    public UsersController(AppDbContext context)
    {
        _context = context;
    }

    // LIST USERS
    [HttpGet]
    public async Task<IActionResult> GetUsers()
    {
        var users = await _context.Users
            .Select(u => new { u.Id, u.Username }) // Don't return password hash!
            .ToListAsync();
        return Ok(users);
    }

    // DELETE USER
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound("User not found");

        if (user.Id == 1) return BadRequest("Cannot delete the Administrator account.");

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
        return Ok(new { message = $"User '{user.Username}' deleted." });
    }

    // CREATE USER
    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest("Username and Password are required.");

        if (await _context.Users.AnyAsync(u => u.Username == req.Username))
            return BadRequest("Username already exists.");

        var newUser = new User 
        { 
            Username = req.Username, 
            PasswordHash = req.Password // Plaintext for MVP 
        };
        
        _context.Users.Add(newUser);
        await _context.SaveChangesAsync();
        
        return Ok(new { id = newUser.Id, username = newUser.Username });
    }

    // RESET PASSWORD
    [HttpPost("{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest req)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound("User not found");

        if (string.IsNullOrWhiteSpace(req.NewPassword)) return BadRequest("Password cannot be empty");

        user.PasswordHash = req.NewPassword;
        await _context.SaveChangesAsync();
        
        return Ok(new { message = $"Password for user '{user.Username}' has been reset." });
    }
}

public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class ResetPasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}
