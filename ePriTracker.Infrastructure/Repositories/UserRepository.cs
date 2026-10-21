using ePriTracker.Domain.Interfaces;
using ePriTracker.Domain.Models.Entities;
using ePriTracker.Infrastructure.Context;
using Microsoft.EntityFrameworkCore;

namespace ePriTracker.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly ePriTrackerContext _context;

    public UserRepository(ePriTrackerContext context)
    {
        _context = context;
    }

    public async Task<User?> GetByEmailAsync(string email)
    {
        return await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower());
    }

    public async Task<User?> GetByEmailWithRolesAsync(string email)
    {
        return await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower());
    }

    public async Task<bool> ExistsByEmailAsync(string email)
    {
        return await _context.Users
            .AnyAsync(u => u.Email.ToLower() == email.ToLower());
    }

    public async Task AddUserAsync(User user)
    {
        await _context.Users.AddAsync(user);
        await _context.SaveChangesAsync();
    }

    public async Task AssignDefaultRoleAsync(long userId, string roleName = "User")
    {
        // 1. Tìm hoặc tự tạo Role "User" nếu DB chưa có
        var role = await _context.Roles.FirstOrDefaultAsync(r => r.RoleName == roleName);
        if (role == null)
        {
            role = new Role { RoleName = roleName };
            await _context.Roles.AddAsync(role);
            await _context.SaveChangesAsync();
        }

        // 2. Thêm bản ghi vào bảng user_roles
        var userRole = new UserRole
        {
            UserId = userId,
            RoleId = role.RoleId,
            AssignedAt = DateTimeOffset.UtcNow
        };

        await _context.UserRoles.AddAsync(userRole);
        await _context.SaveChangesAsync();
    }
}