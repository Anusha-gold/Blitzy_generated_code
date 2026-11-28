using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SmartHotel.Data.Entities;

namespace SmartHotel.Data.Configurations;

/// <summary>
/// Maps <see cref="RoomType"/> to the <c>RoomTypes</c> table, with the check constraints
/// <c>CK_RoomTypes_MaxGuests</c> and <c>CK_RoomTypes_NightlyPrice</c>, the required restricted foreign key to
/// <c>Hotels</c>, and the alternate key <c>AK_RoomTypes_Id_HotelId</c>.
/// </summary>
/// <remarks>
/// <para>
/// The alternate key <c>(Id, HotelId)</c> exists only to be the principal of the composite foreign key
/// <c>FK_Rooms_RoomTypes_RoomTypeId_HotelId</c>, which keeps every room's denormalized <c>HotelId</c> equal to its
/// room type's hotel. That relationship is configured once, in <c>RoomConfiguration</c>. Being part of a key makes
/// <c>HotelId</c> immutable after insert, which is intended: a room type never moves between hotels.
/// </para>
/// <para>
/// Booking creation locks a row with <c>SELECT * FROM "RoomTypes" WHERE "Id" = @id FOR UPDATE</c> and
/// materializes it as a <see cref="RoomType"/>. The statement names the table and its key column literally, so
/// renaming either means rewriting it. Its result must return every column this mapping reads, under the mapped
/// name; extra columns are ignored, so a column may be added once the schema has it.
/// </para>
/// </remarks>
internal sealed class RoomTypeConfiguration : IEntityTypeConfiguration<RoomType>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RoomType> builder)
    {
        builder.ToTable("RoomTypes", t =>
        {
            t.HasCheckConstraint("CK_RoomTypes_MaxGuests", "\"MaxGuests\" >= 1");
            t.HasCheckConstraint("CK_RoomTypes_NightlyPrice", "\"NightlyPrice\" >= 0 AND \"NightlyPrice\" <= 99999.99");
        });

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).UseIdentityByDefaultColumn();

        builder.HasAlternateKey(t => new { t.Id, t.HotelId })
            .HasName("AK_RoomTypes_Id_HotelId");

        builder.Property(t => t.HotelId)
            .IsRequired();

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(t => t.MaxGuests)
            .IsRequired();

        // numeric(7,2) caps finite prices at 99,999.99, the bound PriceCalculator.MaxNightlyPrice enforces in the API.
        // The check's upper bound also rejects NaN, which numeric admits and orders above every finite number.
        builder.Property(t => t.NightlyPrice)
            .IsRequired()
            .HasPrecision(7, 2);

        builder.HasOne(t => t.Hotel)
            .WithMany(h => h.RoomTypes)
            .HasForeignKey(t => t.HotelId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_RoomTypes_Hotels_HotelId");

        // The alternate key leads with Id, so finding a hotel's room types still needs its own index.
        builder.HasIndex(t => t.HotelId)
            .HasDatabaseName("IX_RoomTypes_HotelId");
    }
}