using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Mvc;

using SmartHotel.Data.Entities;

namespace SmartHotel.Api.Modules.Bookings;

// Validation attributes sit on the positional parameters because MVC throws when they target a record's
// properties. The numeric members are int? so that a missing or null JSON property binds null and [Required]
// reports it, instead of a silent 0 or an implicit required check with a different message.
//
// [JsonNumberHandling(Strict)] is there because MVC's JSON options start from JsonSerializerDefaults.Web, which
// also reads a number from a quoted string: "1" would bind as 1 and pass [Required] and [Range]. Under strict
// handling a quoted number is a wrong JSON type, so the input formatter rejects the body with a 400 keyed by its
// JSON path (for example $.hotelId) before validation runs. A missing or null property still binds null for
// [Required]. An attribute on the type takes precedence over the serializer options, so the host's JSON options
// need no change.
//
// [ModelBinder(Name)] sets each parameter's key in the 400 errors object. For positional record parameters MVC
// keys attribute errors by the CLR parameter name (HotelId) and ignores SystemTextJsonValidationMetadataProvider,
// while the error contract keys them by the camelCase JSON name (hotelId). The name does not affect body binding.
//
// CheckIn and CheckOut are strings with no attributes, so StayPeriod.TryCreate, through IsoDate, is the one parser
// for every date input. It owns the validation order and reports a missing or nonconforming date under checkIn or
// checkOut with the same messages as the hotel endpoints. A DateOnly member would instead reject a nonconforming
// date in the input formatter, under $.checkIn and with the formatter's message.

/// <summary>
/// Body of <c>POST /api/bookings</c>. The request never carries a price: the server computes the total from the
/// room type's nightly price and the number of nights. Error messages are fixed sentences that never echo the
/// input. The numeric fields must be JSON numbers: a quoted number is a wrong JSON type and returns 400.
/// </summary>
/// <param name="HotelId">Required, at least 1, and the id of an existing hotel (otherwise 404).</param>
/// <param name="RoomTypeId">
/// Required, at least 1, and the id of a room type of that hotel. An unknown room type, or one that belongs to
/// another hotel, is a 404.
/// </param>
/// <param name="CheckIn">
/// Required, exactly <c>yyyy-MM-dd</c>, and not earlier than the current UTC date.
/// </param>
/// <param name="CheckOut">
/// Required, exactly <c>yyyy-MM-dd</c>, and 1 to 30 nights after <paramref name="CheckIn"/>. The check-out day
/// is not occupied.
/// </param>
/// <param name="Guests">
/// Required and at least 1, unlike the hotel endpoints, where <c>guests</c> defaults to 1. A value above the room
/// type's maximum is a 400 keyed <c>guests</c>.
/// </param>
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CreateBookingRequest(
    [ModelBinder(Name = "hotelId")]
    [Required(ErrorMessage = "hotelId is required.")]
    [Range(1, int.MaxValue, ErrorMessage = "hotelId must be at least 1.")]
    int? HotelId,
    [ModelBinder(Name = "roomTypeId")]
    [Required(ErrorMessage = "roomTypeId is required.")]
    [Range(1, int.MaxValue, ErrorMessage = "roomTypeId must be at least 1.")]
    int? RoomTypeId,
    string? CheckIn,
    string? CheckOut,
    [ModelBinder(Name = "guests")]
    [Required(ErrorMessage = "guests is required.")]
    [Range(1, int.MaxValue, ErrorMessage = "guests must be at least 1.")]
    int? Guests);

/// <summary>
/// A booking as the API returns it: the body of a successful <c>POST /api/bookings</c> (201) and each element of
/// <c>GET /api/bookings</c>. It carries what the booking list and the confirmation banner display, and no data
/// about the guest.
/// </summary>
/// <param name="Id">The booking's id.</param>
/// <param name="HotelId">The id of the hotel the room belongs to.</param>
/// <param name="HotelName">The hotel's display name.</param>
/// <param name="RoomTypeId">The id of the booked room type.</param>
/// <param name="RoomTypeName">The room type's display name, such as <c>Standard</c>.</param>
/// <param name="RoomNumber">The room the server assigned, unique within the hotel.</param>
/// <param name="CheckIn">The first night of the stay, serialized as <c>yyyy-MM-dd</c>.</param>
/// <param name="CheckOut">
/// The departure day, serialized as <c>yyyy-MM-dd</c>. It is the exclusive end of the stay and is not occupied.
/// </param>
/// <param name="Nights">The number of nights, 1 to 30.</param>
/// <param name="Guests">The number of guests, from the booking's guest count.</param>
/// <param name="TotalPrice">
/// The total in USD with two decimals: the nightly price multiplied by <paramref name="Nights"/>, computed on the
/// server when the booking was created.
/// </param>
/// <param name="Status">
/// <see cref="BookingStatus.Confirmed"/> or <see cref="BookingStatus.Cancelled"/>, serialized as its name.
/// </param>
/// <param name="CreatedAt">
/// When the booking was created, as a UTC <see cref="DateTime"/>, so it serializes with a <c>Z</c> suffix. The
/// list is ordered by it, newest first.
/// </param>
public sealed record BookingResponse(
    int Id,
    int HotelId,
    string HotelName,
    int RoomTypeId,
    string RoomTypeName,
    string RoomNumber,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Nights,
    int Guests,
    decimal TotalPrice,
    BookingStatus Status,
    DateTime CreatedAt);

/// <summary>
/// Outcome of a booking-creation attempt: one of the nested records below, which are its supported outcomes.
/// Consumers map each nested record and keep an explicit fallback for any other value.
/// </summary>
/// <remarks>
/// <see cref="InvalidStay"/> carries the stay-validation errors, because the service validates the stay before
/// anything else and the controller makes one service call. <see cref="CapacityExceeded"/> and
/// <see cref="NoRoomAvailable"/> carry the values their error messages state, so the controller needs no second
/// query to build them.
/// </remarks>
public abstract record CreateBookingResult
{
    private CreateBookingResult()
    {
    }

    /// <summary>The booking was created and confirmed (201).</summary>
    /// <param name="Booking">The new booking, with its assigned room and server-computed total.</param>
    public sealed record Created(BookingResponse Booking) : CreateBookingResult;

    /// <summary>
    /// The dates break a stay rule (400): a date is missing or not exactly <c>yyyy-MM-dd</c>, check-out is not
    /// after check-in, check-in is earlier than the current UTC date, or the stay exceeds 30 nights.
    /// </summary>
    /// <param name="Errors">The validation errors, keyed <c>checkIn</c> and <c>checkOut</c>.</param>
    public sealed record InvalidStay(IReadOnlyDictionary<string, string[]> Errors) : CreateBookingResult;

    /// <summary>No hotel has the requested id (404).</summary>
    public sealed record HotelNotFound : CreateBookingResult;

    /// <summary>
    /// No room type has the requested id, or the room type belongs to another hotel (404).
    /// </summary>
    public sealed record RoomTypeNotFound : CreateBookingResult;

    /// <summary>The guest count exceeds the room type's capacity (400, keyed <c>guests</c>).</summary>
    /// <param name="MaxGuests">The room type's maximum number of guests.</param>
    public sealed record CapacityExceeded(int MaxGuests) : CreateBookingResult;

    /// <summary>Every room of the type has a confirmed booking that overlaps the stay (409).</summary>
    /// <param name="RoomTypeName">The room type's display name.</param>
    /// <param name="CheckIn">The requested check-in date.</param>
    /// <param name="CheckOut">The requested check-out date, the exclusive end of the stay.</param>
    public sealed record NoRoomAvailable(string RoomTypeName, DateOnly CheckIn, DateOnly CheckOut)
        : CreateBookingResult;
}

/// <summary>
/// Outcome of a cancellation attempt: one of the nested records below, which are its supported outcomes.
/// Consumers map each nested record and keep an explicit fallback for any other value.
/// </summary>
public abstract record CancelResult
{
    private CancelResult()
    {
    }

    /// <summary>The booking is now cancelled and its room is free again (204).</summary>
    public sealed record Cancelled : CancelResult;

    /// <summary>No booking has the requested id (404).</summary>
    public sealed record NotFound : CancelResult;

    /// <summary>The booking belongs to another guest (403).</summary>
    public sealed record Forbidden : CancelResult;

    /// <summary>
    /// The booking was already cancelled (409), including by a concurrent request that cancelled it first.
    /// </summary>
    public sealed record AlreadyCancelled : CancelResult;

    /// <summary>
    /// The check-in date is earlier than the current UTC date (409). A stay that starts today can still be
    /// cancelled until the end of today (UTC).
    /// </summary>
    public sealed record CheckInPassed : CancelResult;
}