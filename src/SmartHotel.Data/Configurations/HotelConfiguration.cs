using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SmartHotel.Data.Entities;

namespace SmartHotel.Data.Configurations;

/// <summary>
/// Maps <see cref="Hotel"/> to the <c>Hotels</c> table: an identity key and four required, length-bounded text columns.
/// </summary>
/// <remarks>
/// The city lookup index <c>IX_Hotels_City_Lower</c> on <c>lower("City")</c> is written by hand in the
/// <c>InitialCreate</c> migration, because EF Core cannot model expression indexes. No index on <c>City</c> is declared
/// here, since a plain model index would add a second, unused one. The relationship to room types is configured once,
/// in <c>RoomTypeConfiguration</c>.
/// </remarks>
internal sealed class HotelConfiguration : IEntityTypeConfiguration<Hotel>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Hotel> builder)
    {
        builder.ToTable("Hotels");

        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).UseIdentityByDefaultColumn();

        builder.Property(h => h.Name).IsRequired().HasMaxLength(200);

        // The availability search and the hand-written IX_Hotels_City_Lower index both match on lower("City").
        builder.Property(h => h.City).IsRequired().HasMaxLength(100);

        builder.Property(h => h.Address).IsRequired().HasMaxLength(300);

        builder.Property(h => h.Description).IsRequired().HasMaxLength(2000);
    }
}