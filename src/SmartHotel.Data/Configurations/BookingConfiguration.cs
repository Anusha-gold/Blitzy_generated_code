using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SmartHotel.Data.Entities;

namespace SmartHotel.Data.Configurations;

/// <summary>
/// Maps <see cref="Booking"/> to the <c>Bookings</c> table, with the check constraints <c>CK_Bookings_Dates</c>,
/// <c>CK_Bookings_GuestCount</c> and <c>CK_Bookings_Status</c>, required restricted foreign keys to <c>Guests</c> and
/// <c>Rooms</c>, and the indexes <c>IX_Bookings_RoomId_CheckIn_CheckOut</c> and <c>IX_Bookings_GuestId_CreatedAt</c>.
/// </summary>
/// <remarks>
/// <para>
/// A booking occupies the half-open range from <c>CheckIn</c> inclusive to <c>CheckOut</c> exclusive, so its
/// check-out day is free for the next stay of the same room.
/// </para>
/// <para>
/// The guarantee against double booking is the exclusion constraint <c>EX_Bookings_Room_NoOverlap</c>, a GiST
/// constraint over <c>"RoomId"</c> and <c>daterange("CheckIn", "CheckOut", '[)')</c> that applies only to
/// <c>Confirmed</c> rows. It is written by hand in the <c>InitialCreate</c> migration, because EF Core cannot model
/// exclusion constraints, and it is deliberately not declared here. <c>CK_Bookings_Dates</c> must stay beside it,
/// requiring check-out strictly after check-in: <c>daterange</c> already rejects reversed dates, but equal dates form
/// an empty range, which overlaps nothing and would evade the exclusion constraint.
/// </para>
/// <para>
/// <c>Status</c> is stored as the <see cref="BookingStatus"/> member name. The exclusion constraint's filter,
/// <c>CK_Bookings_Status</c> and the API's availability and cancellation queries all compare it with the literals
/// <c>'Confirmed'</c> and <c>'Cancelled'</c>.
/// </para>
/// <para>
/// <c>CheckIn</c> and <c>CheckOut</c> are stored and compared as the exact calendar dates, because the hosts disable
/// Npgsql's infinity conversion. <c>CK_Bookings_Dates</c>, the exclusion constraint and the overlap queries therefore
/// see a check-out of 9999-12-31 as that date, never as <c>infinity</c>.
/// </para>
/// </remarks>
internal sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("Bookings", t =>
        {
            t.HasCheckConstraint("CK_Bookings_Dates", "\"CheckOut\" > \"CheckIn\"");
            t.HasCheckConstraint("CK_Bookings_GuestCount", "\"GuestCount\" >= 1");
            t.HasCheckConstraint("CK_Bookings_Status", "\"Status\" IN ('Confirmed','Cancelled')");
        });

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).UseIdentityByDefaultColumn();

        builder.Property(b => b.GuestId)
            .IsRequired();

        builder.Property(b => b.RoomId)
            .IsRequired();

        builder.Property(b => b.CheckIn)
            .IsRequired();

        builder.Property(b => b.CheckOut)
            .IsRequired();

        builder.Property(b => b.GuestCount)
            .IsRequired();

        // numeric(10,2) holds the largest total PriceCalculator can produce: 30 nights at 99,999.99.
        builder.Property(b => b.TotalPrice)
            .IsRequired()
            .HasPrecision(10, 2);

        // No database default: BookingService always sets the status, and a default would make EF omit the CLR
        // default Confirmed from inserts as if it were unset.
        builder.Property(b => b.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(b => b.CreatedAt)
            .IsRequired();

        builder.HasOne(b => b.Guest)
            .WithMany()
            .HasForeignKey(b => b.GuestId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Bookings_Guests_GuestId");

        builder.HasOne(b => b.Room)
            .WithMany()
            .HasForeignKey(b => b.RoomId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Bookings_Rooms_RoomId");

        // Both indexes lead with a foreign-key column, so they also cover the two foreign keys and EF adds no
        // separate single-column index for either.
        builder.HasIndex(b => new { b.RoomId, b.CheckIn, b.CheckOut })
            .HasDatabaseName("IX_Bookings_RoomId_CheckIn_CheckOut");

        builder.HasIndex(b => new { b.GuestId, b.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_Bookings_GuestId_CreatedAt");
    }
}