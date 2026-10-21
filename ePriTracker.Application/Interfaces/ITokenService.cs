using ePriTracker.Domain.Models.Entities;

namespace ePriTracker.Application.Interfaces;

public interface ITokenService
{
    string GenerateToken(User user);
}