using SmartHotel.Api.Common.Stays;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.UnitTests.Stays;

public sealed class StayRulesTests
{
    // The existing stay occupies the nights of 10 and 11 December; 12 December, its check-out day, is free.
    private static readonly DateOnly ExistingCheckIn = new(2026, 12, 10);
    private static readonly DateOnly ExistingCheckOut = new(2026, 12, 12);

    // A requested stay across a month and a year boundary: the nights of 30 and 31 December 2026 and 1 January 2027;
    // 2 January 2027, its check-out day, is free.
    private static readonly DateOnly CrossMonthCheckIn = new(2026, 12, 30);
    private static readonly DateOnly CrossMonthCheckOut = new(2027, 1, 2);

    // Existing bookings with varied dates, each with whether it overlaps the cross-month stay. On every overlapping
    // row, comparing only the day of the month gives false, so only whole-date comparisons pass.
    private static readonly (DateOnly CheckIn, DateOnly CheckOut, bool Expected)[] CrossMonthBookings =
    [
        (new(2026, 12, 27), new(2026, 12, 30), false), // existing check-out equals the new check-in
        (new(2027, 1, 2), new(2027, 1, 5), false),     // new check-out equals the existing check-in
        (new(2026, 12, 29), new(2026, 12, 31), true),  // partial overlap at the start, within December
        (new(2027, 1, 1), new(2027, 1, 4), true),      // partial overlap at the end, within January
        (new(2026, 12, 31), new(2027, 1, 1), true),    // contained range across the year boundary
        (new(2026, 12, 28), new(2027, 1, 3), true),    // enclosing range across the year boundary
        (new(2026, 11, 28), new(2026, 12, 1), false),  // disjoint, before
        (new(2027, 1, 5), new(2027, 1, 8), false),     // disjoint, after
    ];

    /// <summary>
    /// Requested stays as (check-in day, check-out day) in December 2026, each with whether it overlaps the
    /// existing stay from the 10th to the 12th.
    /// </summary>
    public static TheoryData<int, int, bool> OverlapCases => new()
    {
        { 10, 12, true },  // identical ranges
        { 9, 11, true },   // partial overlap at the start
        { 11, 13, true },  // partial overlap at the end
        { 10, 11, true },  // contained range
        { 9, 13, true },   // enclosing range
        { 12, 14, false }, // existing check-out equals the new check-in: the check-out day is free
        { 8, 10, false },  // new check-out equals the existing check-in
        { 13, 15, false }, // disjoint, after
        { 5, 7, false },   // disjoint, before
    };

    /// <summary>
    /// Existing bookings as (check-in, check-out), each with whether it overlaps the requested stay from
    /// 30 December 2026 to 2 January 2027.
    /// </summary>
    public static TheoryData<DateOnly, DateOnly, bool> CrossMonthOverlapCases => new(CrossMonthBookings);

    [Theory, MemberData(nameof(OverlapCases))]
    public void Overlaps_RequestedRange_MatchesHalfOpenRule(
        int requestedCheckInDay,
        int requestedCheckOutDay,
        bool expected)
    {
        Assert.Equal(
            expected,
            StayRules.Overlaps(ExistingCheckIn, ExistingCheckOut, Day(requestedCheckInDay), Day(requestedCheckOutDay)));
    }

    [Theory, MemberData(nameof(OverlapCases))]
    public void Overlaps_SwappedRanges_GivesSameResult(
        int requestedCheckInDay,
        int requestedCheckOutDay,
        bool expected)
    {
        Assert.Equal(
            expected,
            StayRules.Overlaps(Day(requestedCheckInDay), Day(requestedCheckOutDay), ExistingCheckIn, ExistingCheckOut));
    }

    [Theory, MemberData(nameof(CrossMonthOverlapCases))]
    public void Overlaps_CrossMonthRanges_MatchesHalfOpenRuleInEitherOrder(
        DateOnly existingCheckIn,
        DateOnly existingCheckOut,
        bool expected)
    {
        Assert.Equal(
            expected,
            StayRules.Overlaps(existingCheckIn, existingCheckOut, CrossMonthCheckIn, CrossMonthCheckOut));
        Assert.Equal(
            expected,
            StayRules.Overlaps(CrossMonthCheckIn, CrossMonthCheckOut, existingCheckIn, existingCheckOut));
    }

    [Theory, MemberData(nameof(OverlapCases))]
    public void ConfirmedOverlap_ConfirmedBooking_AgreesWithOverlaps(
        int requestedCheckInDay,
        int requestedCheckOutDay,
        bool expected)
    {
        Func<Booking, bool> predicate = StayRules
            .ConfirmedOverlap(new StayPeriod(Day(requestedCheckInDay), Day(requestedCheckOutDay)))
            .Compile();

        bool actual = predicate(CreateBooking(BookingStatus.Confirmed));

        Assert.Equal(expected, actual);
        Assert.Equal(
            StayRules.Overlaps(ExistingCheckIn, ExistingCheckOut, Day(requestedCheckInDay), Day(requestedCheckOutDay)),
            actual);
    }

    [Fact]
    public void ConfirmedOverlap_OnePredicateOverVariedBookings_AgreesWithOverlapsForEach()
    {
        // One compiled predicate serves every booking, so a predicate that ignores a booking's own dates fails a row.
        Func<Booking, bool> predicate = StayRules
            .ConfirmedOverlap(new StayPeriod(CrossMonthCheckIn, CrossMonthCheckOut))
            .Compile();

        Assert.All(CrossMonthBookings, booking =>
        {
            bool actual = predicate(CreateBooking(BookingStatus.Confirmed, booking.CheckIn, booking.CheckOut));

            Assert.Equal(booking.Expected, actual);
            Assert.Equal(
                StayRules.Overlaps(booking.CheckIn, booking.CheckOut, CrossMonthCheckIn, CrossMonthCheckOut),
                actual);
        });
    }

    [Theory, MemberData(nameof(OverlapCases))]
    public void ConfirmedOverlap_CancelledBooking_IsAlwaysFalse(
        int requestedCheckInDay,
        int requestedCheckOutDay,
        bool expected)
    {
        Func<Booking, bool> predicate = StayRules
            .ConfirmedOverlap(new StayPeriod(Day(requestedCheckInDay), Day(requestedCheckOutDay)))
            .Compile();

        // The confirmed control shows the dates alone give the expected result, so on the overlapping rows the
        // status is the only reason the cancelled booking does not block the stay.
        Assert.Equal(expected, predicate(CreateBooking(BookingStatus.Confirmed)));
        Assert.False(predicate(CreateBooking(BookingStatus.Cancelled)));
    }

    [Fact]
    public void MaxNights_IsThirty()
    {
        Assert.Equal(30, StayRules.MaxNights);
    }

    private static DateOnly Day(int day)
    {
        return new DateOnly(2026, 12, day);
    }

    private static Booking CreateBooking(BookingStatus status)
    {
        return CreateBooking(status, ExistingCheckIn, ExistingCheckOut);
    }

    private static Booking CreateBooking(BookingStatus status, DateOnly checkIn, DateOnly checkOut)
    {
        return new Booking
        {
            RoomId = 1,
            CheckIn = checkIn,
            CheckOut = checkOut,
            Status = status,
        };
    }
}