using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GomokuClient.Web.Security;

public sealed class AuthDbContext : IdentityDbContext<IdentityUser>
{
    private readonly IDataProtectionProvider _protectionProvider;

    public AuthDbContext(DbContextOptions<AuthDbContext> options, IDataProtectionProvider protectionProvider)
        : base(options) => _protectionProvider = protectionProvider;

    public DbSet<GameMembership> GameMemberships => Set<GameMembership>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Identity stores the authenticator key in the user-token table; encrypt token values at rest.
        var protector = _protectionProvider.CreateProtector("Gomoku.Identity.AuthenticatorKey.v1");
        var converter = new ValueConverter<string?, string?>(
            value => value == null ? null : protector.Protect(value),
            value => value == null ? null : protector.Unprotect(value));
        builder.Entity<IdentityUserToken<string>>()
            .Property(token => token.Value)
            .HasConversion(converter);

        builder.Entity<GameMembership>()
            .HasKey(membership => new { membership.UserId, membership.RoomId });
        builder.Entity<GameMembership>()
            .HasIndex(membership => membership.PlayerId)
            .IsUnique();
        builder.Entity<GameMembership>()
            .HasOne<IdentityUser>()
            .WithMany()
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
