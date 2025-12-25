using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;

    public AuthController(AppDbContext context, IConfiguration configuration, IMemoryCache cache)
    {
        _context = context;
        _configuration = configuration;
        _cache = cache;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var failKey = $"login_fail_{request.Username}";
        _cache.TryGetValue(failKey, out int failedCount);

        // Check if captcha is required
        if (failedCount >= 3)
        {
            if (string.IsNullOrEmpty(request.CaptchaId) || 
                string.IsNullOrEmpty(request.CaptchaAnswer))
            {
               return GenerateCaptchaResponse("验证码必填");
            }

            var captchaKey = $"captcha_{request.CaptchaId}";
            if (!_cache.TryGetValue(captchaKey, out int expectedAnswer))
            {
                return GenerateCaptchaResponse("验证码已过期");
            }

            if (!int.TryParse(request.CaptchaAnswer, out int userAnswer) || userAnswer != expectedAnswer)
            {
                // Wrong answer
                return GenerateCaptchaResponse("验证码错误");
            }

            // Valid captcha, cleanup
            _cache.Remove(captchaKey);
        }

        // For MVP, create admin user if not exists
        if (!await _context.Users.AnyAsync())
        {
            _context.Users.Add(new User { Username = "admin", PasswordHash = "admin" }); // Plaintext for MVP/Demo
            await _context.SaveChangesAsync();
        }

        var user = await _context.Users.SingleOrDefaultAsync(u => u.Username == request.Username);
        if (user == null || user.PasswordHash != request.Password)
        {
            failedCount++;
            _cache.Set(failKey, failedCount, TimeSpan.FromMinutes(15));

            if (failedCount >= 3)
            {
                return GenerateCaptchaResponse("用户名或密码错误");
            }
            return Unauthorized("Invalid credentials");
        }

        // Login Success
        _cache.Remove(failKey);

        var token = GenerateJwtToken(user);
        return Ok(new { token });
    }

    private IActionResult GenerateCaptchaResponse(string message)
    {
        // Dict: 0-10
        string[] cnNums = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
        var rnd = new Random();
        // Generate A and B such that B >= A (Posterior - Anterior >= 0)
        int a = rnd.Next(0, 11);
        int b = rnd.Next(a, 11);
        
        var answer = b - a;
        var questionText = $"{cnNums[a]} {cnNums[b]}"; // Anterior Posterior
        var captchaId = Guid.NewGuid().ToString("N");

        _cache.Set($"captcha_{captchaId}", answer, TimeSpan.FromMinutes(5));

        // Return 401 with special payload
        // Clients should check for 'captchaId' and 'captchaText' in the response body
        return Unauthorized(new { 
            message, 
            captchaRequired = true, 
            captchaId, 
            captchaText = questionText 
        });
    }

    private string GenerateJwtToken(User user)
    {
        var keyStr = _configuration["Jwt:Key"] ?? "YOUR_SECURE_SECRET_KEY_MUST_BE_LONG_ENOUGH";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyStr));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Name, user.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        // Determine Role
        if (user.Id == 1) 
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }
        else if (user.Username == "demo")
        {
            claims.Add(new Claim(ClaimTypes.Role, "Demo"));
        }
        else
        {
            claims.Add(new Claim(ClaimTypes.Role, "User"));
        }

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.Now.AddDays(7),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userIdStr = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out int userId))
        {
            return Unauthorized();
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound("User not found");

        if (user.PasswordHash != request.OldPassword)
        {
            return BadRequest("Incorrect old password");
        }

        user.PasswordHash = request.NewPassword;
        await _context.SaveChangesAsync();

        return Ok(new { message = "Password updated successfully" });
    }
}

public class ChangePasswordRequest
{
    public required string OldPassword { get; set; }
    public required string NewPassword { get; set; }
}

public class LoginRequest
{
    public required string Username { get; set; }
    public required string Password { get; set; }
    public string? CaptchaId { get; set; }
    public string? CaptchaAnswer { get; set; }
}
