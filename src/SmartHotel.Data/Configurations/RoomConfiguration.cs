using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SmartHotel.Data.Entities;

namespace SmartHotel.Data.Configurations;

/// <summary>
/// Maps <see cref="Room"/> to the <c>Rooms</c> table: an identity key, the required room type and hotel identifiers,
/// and a room number of at most 10 characters that is unique within its hotel.
/// </summary>
/// <remarks>
/// <para>
/// <c>HotelId</c> is a denormalized copy of the room type's hotel. It is constrained only through the composite
/// foreign key <c>FK_Rooms_RoomTypes_RoomTypeId_HotelId</c> on <c>(RoomTypeId, HotelId)</c>, whose principal is the
/// alternate key <c>AK_RoomTypes_Id_HotelId</c> on <c>(Id, HotelId)</c>, so it can never disagree with the room type.
/// That lets the unique index <c>IX_Rooms_HotelId_RoomNumber</c> enforce per-hotel room numbers declaratively, which
/// an index on the room type alone cannot do. There is deliberately no separate foreign key to <c>Hotels</c>.
/// </para>
/// <para>
/// Because the principal key carries both columns, saving rooms added through <see cref="RoomType.Rooms"/> fills in
/// both <c>RoomTypeId</c> and <c>HotelId</c>. The relationship from bookings to rooms is configured in
/// <c>BookingConfiguration</c>, since a room has no collection of bookings.
/// </para>
/// </remarks>
internal sealed class RoomConfiguration : IEntityTypeConfiguration<Room>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Room> builder)
    {
        builder.ToTable("Rooms");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).UseIdentityByDefaultColumn();

        builder.Property(r => r.RoomTypeId)
            .IsRequired();

        builder.Property(r => r.HotelId)
            .IsRequired();

        builder.Property(r => r.RoomNumber)
            .IsRequired()
            .HasMaxLength(10);

        builder.HasOne(r => r.RoomType)
            .WithMany(t => t.Rooms)
            .HasForeignKey(r => new { r.RoomTypeId, r.HotelId })
            .HasPrincipalKey(t => new { t.Id, t.HotelId })
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Rooms_RoomTypes_RoomTypeId_HotelId");

        builder.HasIndex(r => new { r.RoomTypeId, r.HotelId })
            .HasDatabaseName("IX_Rooms_RoomTypeId_HotelId");

        builder.HasIndex(r => new { r.HotelId, r.RoomNumber })
            .IsUnique()
            .HasDatabaseName("IX_Rooms_HotelId_RoomNumber");
    }
}