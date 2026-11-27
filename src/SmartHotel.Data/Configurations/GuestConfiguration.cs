using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SmartHotel.Data.Entities;

namespace SmartHotel.Data.Configurations;

/// <summary>
/// Maps <see cref="Guest"/> to the <c>Guests</c> table, with the unique index <c>UX_Guests_Email</c>
/// and the check constraint <c>CK_Guests_Email_Normalized</c>.
/// </summary>
/// <remarks>
/// The database never folds case. The API's <c>AuthService.NormalizeEmail</c> trims and lower-cases
/// every email before it is stored or looked up, so the ordinary unique index enforces
/// case-insensitive uniqueness. The check constraint is the database backstop for that rule: it
/// refuses any stored value outside printable ASCII or containing an upper-case letter, evaluated
/// under <c>COLLATE "C"</c> so the outcome does not depend on the database's default collation.
/// </remarks>
internal sealed class GuestConfiguration : IEntityTypeConfiguration<Guest>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Guest> builder)
    {
        builder.ToTable("Guests", t => t.HasCheckConstraint(
            "CK_Guests_Email_Normalized",
            """("Email" COLLATE "C") ~ '^[!-~]+$' AND ("Email" COLLATE "C") !~ '[A-Z]'"""));

        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).UseIdentityByDefaultColumn();

        builder.Property(g => g.Email)
            .IsRequired()
            .HasMaxLength(254);

        builder.Property(g => g.PasswordHash)
            .IsRequired();

        builder.Property(g => g.FullName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(g => g.CreatedAt)
            .IsRequired();

        // The name comes from the shared constant that AuthService also matches: it maps a 23505 to 409 only
        // when the violation's ConstraintName is this index.
        builder.HasIndex(g => g.Email)
            .IsUnique()
            .HasDatabaseName(Guest.EmailUniqueIndexName);
    }
}