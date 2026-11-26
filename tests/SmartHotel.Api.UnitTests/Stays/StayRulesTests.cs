using SmartHotel.Api.Common.Stays;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.UnitTests.Stays;

public sealed class StayRulesTests
{
    // The existing stay occupies the nights of 10 and 11 December; 12 December, its check-out day, is free.
    private static readonly DateOnly ExistingCheckIn = new(2026, 12, 10);
    private static readonly DateOnly ExistingCheckOut = new(2026, 12, 12);

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
        return new Booking
        {
            RoomId = 1,
            CheckIn = ExistingCheckIn,
            CheckOut = ExistingCheckOut,
            Status = status,
        };
    }
}