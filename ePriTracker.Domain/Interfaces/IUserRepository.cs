using ePriTracker.Domain.Models.Entities;

namespace ePriTracker.Domain.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string email);
    Task<User?> GetByEmailWithRolesAsync(string email); // Thêm để load User kèm Roles
    Task<bool> ExistsByEmailAsync(string email);
    Task AddUserAsync(User user);
    Task AssignDefaultRoleAsync(long userId, string roleName = "User"); // Thêm để lưu UserRole
}