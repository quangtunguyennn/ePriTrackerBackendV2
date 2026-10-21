using ePriTracker.Application.DTOs.Auth;
using ePriTracker.Application.Interfaces;
using ePriTracker.Domain.Interfaces;
using ePriTracker.Domain.Models.Entities;

namespace ePriTracker.Application.Services;

public class AuthService : IAuthService
{
    private readonly IUserRepository _userRepo;
    private readonly ITokenService _tokenService;

    public AuthService(IUserRepository userRepo, ITokenService tokenService)
    {
        _userRepo = userRepo;
        _tokenService = tokenService;
    }

    public async Task<AuthResponseDto> RegisterAsync(RegisterRequestDto request)
    {
        if (await _userRepo.ExistsByEmailAsync(request.Email))
        {
            throw new InvalidOperationException("Email account is already registered.");
        }

        var user = new User
        {
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // 1. Lưu User mới
        await _userRepo.AddUserAsync(user);

        // 2. Gán Role mặc định "User" vào bảng user_roles
        await _userRepo.AssignDefaultRoleAsync(user.UserId, "User");

        // 3. Load lại User kèm Role để sinh Token chuẩn
        var registeredUser = await _userRepo.GetByEmailWithRolesAsync(user.Email) ?? user;

        var token = _tokenService.GenerateToken(registeredUser);
        return new AuthResponseDto
        {
            Token = token,
            Email = user.Email,
            FullName = $"{user.FirstName} {user.LastName}".Trim(),
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };
    }

    public async Task<AuthResponseDto> LoginAsync(LoginRequestDto request)
    {
        // Load User kèm theo danh sách Roles
        var user = await _userRepo.GetByEmailWithRolesAsync(request.Email);

        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        var token = _tokenService.GenerateToken(user);
        return new AuthResponseDto
        {
            Token = token,
            Email = user.Email,
            FullName = $"{user.FirstName} {user.LastName}".Trim(),
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };
    }
}