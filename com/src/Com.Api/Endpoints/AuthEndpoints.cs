using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Com.Shared.Contracts;
using Com.Shared.Data;
using Com.Shared.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Com.Api.Endpoints;

public static class ClaimsExtensions
{
    public static string Upn(this ClaimsPrincipal p)
        => p.FindFirst("sub")?.Value ?? p.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
    public static string Role(this ClaimsPrincipal p)
        => p.FindFirst("role")?.Value ?? "";
    public static string DisplayName(this ClaimsPrincipal p)
        => p.FindFirst("name")?.Value ?? p.Upn();
}

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest req, ComDbContext db) =>
        {
            var upn = (req.Upn ?? "").Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Upn == upn && u.Active);
            if (user == null || !PasswordHasher.Verify(req.Password ?? "", user.PasswordHash, user.PasswordSalt))
                return Results.Json(new { code = "invalid_credentials", message = "账号或密码不正确" }, statusCode: 401);

            var token = IssueToken(user);
            return Results.Ok(new LoginResponse(token, new UserDto(user.Upn, user.DisplayName, user.Role, Roles.Label(user.Role))));
        });

        app.MapGet("/api/auth/me", async (ClaimsPrincipal p, ComDbContext db) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Upn == p.Upn());
            return u == null
                ? Results.NotFound()
                : Results.Ok(new UserDto(u.Upn, u.DisplayName, u.Role, Roles.Label(u.Role)));
        }).RequireAuthorization();

        // 演示账号清单：写死在前端会显得不真实，由服务端提供
        app.MapGet("/api/auth/demo-accounts", () => Results.Ok(new[]
        {
            new { upn = "sales@com.demo",    name = "张伟 · 华东销售", role = "sales",     roleLabel = "销售" },
            new { upn = "director@com.demo", name = "李强 · 销售总监", role = "approver1", roleLabel = "销售总监" },
            new { upn = "finance@com.demo",  name = "王敏 · 财务经理", role = "finance",   roleLabel = "财务" },
            new { upn = "gm@com.demo",       name = "陈静 · 总经理",   role = "admin",     roleLabel = "总经理" }
        }));
    }

    private static string IssueToken(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Upn),
            new("name", user.DisplayName),
            new("role", user.Role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(JwtKeys.Key), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "com", audience: "com-portal", claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class MasterDataEndpoints
{
    public static void MapMasterDataEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/customers", async (ComDbContext db) =>
            Results.Ok(await db.Customers.OrderBy(c => c.Code)
                .Select(c => new CustomerDto(c.Id, c.Code, c.Name, c.TaxId, c.CreditLimit)).ToListAsync()))
            .RequireAuthorization();

        app.MapGet("/api/products", async (ComDbContext db) =>
            Results.Ok(await db.Products.Where(p => p.Active).OrderBy(p => p.Sku)
                .Select(p => new ProductDto(p.Id, p.Sku, p.Name, p.UnitPrice, p.Currency)).ToListAsync()))
            .RequireAuthorization();
    }
}
