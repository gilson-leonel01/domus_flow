using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace DomusFlow.Api.Security;

public sealed class JwtTokenService(IConfiguration configuration)
{
    private readonly string _secret = configuration["JWT_SECRET"]
        ?? configuration["Jwt:Secret"]
        ?? throw new InvalidOperationException("JWT secret is not configured.");

    private readonly string _issuer = configuration["Jwt:Issuer"] ?? "DomusFlow";
    private readonly string _audience = configuration["Jwt:Audience"] ?? "DomusFlow.Web";
    private readonly int _expirationHours = configuration.GetValue("Jwt:ExpirationHours", 24);

    public string Create(string userId, string householdId, string role)
    {
        var now = DateTime.UtcNow;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim("uid", userId),
            new Claim("hid", householdId),
            new Claim("role", role),
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Role, role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: now,
            expires: now.AddHours(_expirationHours),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
