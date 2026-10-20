using Com.Shared.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Security.Cryptography;

namespace Com.Shared.Data;

public class ComDbContext : DbContext
{
    /// <summary>SQLite 不支持 ORDER BY / 比较 DateTimeOffset，用二进制转换器落成可排序的整数。</summary>
    public static bool SqliteCompatMode { get; set; }

    public ComDbContext(DbContextOptions<ComDbContext> options) : base(options) { }

    protected override void ConfigureConventions(ModelConfigurationBuilder cb)
    {
        if (!SqliteCompatMode) return;
        cb.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        cb.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<ApprovalTask> ApprovalTasks => Set<ApprovalTask>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<IntakeDocument> IntakeDocuments => Set<IntakeDocument>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Upn).IsUnique();
            e.Property(x => x.Upn).HasMaxLength(120);
            e.Property(x => x.Role).HasMaxLength(40);
        });

        b.Entity<Customer>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.CreditLimit).HasPrecision(18, 2);
        });

        b.Entity<Product>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Sku).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
        });

        b.Entity<Order>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OrderNo).IsUnique();
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.Property(x => x.TotalAmount).HasPrecision(18, 2);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.OrderNo).HasMaxLength(40);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderLine>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OrderId, x.LineNo }).IsUnique();
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
        });

        b.Entity<ApprovalTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OrderId, x.Level });
            e.HasIndex(x => new { x.ApproverUpn, x.Decision });
        });

        b.Entity<Contract>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.BlobPath).IsUnique();
            e.Property(x => x.BlobPath).HasMaxLength(400);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAt });
        });

        b.Entity<Notification>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserUpn, x.Read });
        });

        b.Entity<IntakeDocument>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(300);
        });
    }
}

/// <summary>PBKDF2 密码散列。演示账号用，生产应接 Entra ID。</summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;

    public static (string Hash, string Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string hash, string salt)
    {
        try
        {
            var s = Convert.FromBase64String(salt);
            var expected = Convert.FromBase64String(hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, s, Iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }
}
