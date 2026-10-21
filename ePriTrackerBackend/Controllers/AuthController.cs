using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;

namespace ePriTrackerBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly ePriTrackerContext _context;
        private readonly IConfiguration _configuration;

        public AuthController(ePriTrackerContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [HttpPost("/api/auth/login")]
        public async Task<IActionResult> Login([FromBody] LoginRequestDTO request)
        {
            // Tìm user và Join với UserRole & Role để lấy danh sách quyền
            var user = await _context.User
                .FirstOrDefaultAsync(u => u.Email == request.Email && u.Password == request.Password);

            if (user == null)
                return Unauthorized(new { message = "Invalid email or password." });

            // Lấy danh sách Role của user từ bảng trung gian UserRole
            var roles = await _context.UserRole
                .Where(ur => ur.UserId == user.UserId)
                .Join(_context.Role,
                      ur => ur.RoleId,
                      r => r.RoleId,
                      (ur, r) => r.RoleName)
                .ToListAsync();

            var token = GenerateJwtToken(user, roles);

            return Ok(new
            {
                token,
                userRoles = roles
            });
        }

        private string GenerateJwtToken(User user, List<string> roles)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.Email)
            };

            // Thêm tất cả các role vào Claims để [Authorize(Roles = "...")] hoạt động chính xác
            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JwtSettings:SecretKey"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["JwtSettings:Issuer"],
                audience: _configuration["JwtSettings:Audience"],
                claims: claims,
                expires: DateTime.Now.AddMinutes(60),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        [HttpPost("/api/auth/register")]
        public async Task<IActionResult> Register([FromBody] registerRequestDTO request)
        {
            if (await _context.User.FirstOrDefaultAsync(x => x.Email == request.Email) != null)
            {
                return BadRequest(new { message = "Email is already in use." });
            }

            // 1. Tạo User mới
            var newUser = new User()
            {
                UserId = Guid.NewGuid(),
                FirstName = request.FirstName,
                LastName = request.LastName,
                Email = request.Email,
                Password = request.Password, // Lưu ý: Nên mã hóa mật khẩu (BCrypt / PasswordHasher) trong môi trường thực tế
                IsActive = true,
                CreatedAt = DateTime.Now
            };

            await _context.User.AddAsync(newUser);

            // 2. Gán mặc định Role "User" cho tài khoản mới đăng ký
            var defaultRole = await _context.Role.FirstOrDefaultAsync(r => r.RoleName == "User");
            if (defaultRole != null)
            {
                var userRole = new UserRole()
                {
                    Id = Guid.NewGuid(),
                    UserId = newUser.UserId,
                    RoleId = defaultRole.RoleId
                };
                await _context.UserRole.AddAsync(userRole);
            }

            await _context.SaveChangesAsync();

            return Ok(new { message = "Register successfully!" });
        }

        [HttpGet("/api/auth/me")]
        [Authorize(Roles = "User")]
        public async Task<IActionResult> GetMe()
        {
            var currentUserEmail = User?.Identity?.Name;

            var user = await _context.User.FirstOrDefaultAsync(x => x.Email == currentUserEmail);

            if (user == null) return NotFound(new { message = "User not found." });

            // Lấy danh sách Role của user hiện tại
            var roles = await _context.UserRole
                .Where(ur => ur.UserId == user.UserId)
                .Join(_context.Role,
                      ur => ur.RoleId,
                      r => r.RoleId,
                      (ur, r) => r.RoleName)
                .ToListAsync();

            var userName = $"{user.FirstName} {user.LastName}";

            return Ok(new
            {
                UserName = userName,
                UserEmail = user.Email,
                Roles = roles
            });
        }
    }
}