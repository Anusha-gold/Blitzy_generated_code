using System.Diagnostics;
using System.Globalization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using SmartHotel.Api.Common.Http;
using SmartHotel.Api.Common.Security;
using SmartHotel.Api.Common.Stays;

namespace SmartHotel.Api.Modules.Bookings;

/// <summary>
/// The guest-only booking endpoints under <c>api/bookings</c>: create a booking, list the caller's own bookings and
/// cancel one of them.
/// </summary>
/// <remarks>
/// <para>
/// <c>[Authorize]</c> applies the default JwtBearer scheme to every action. Authorization runs before model binding,
/// so a request whose bearer token is missing, invalid or expired, or carries no valid guest id in <c>sub</c>, gets
/// the challenge's 401, written as <c>application/problem+json</c> with its <c>WWW-Authenticate: Bearer</c> header
/// kept, even when its body is invalid. The caller's guest id always comes from the token's <c>sub</c> claim, never
/// from the request.
/// </para>
/// <para>
/// Each action binds its input, calls one <see cref="BookingService"/> method and maps the outcome through
/// <see cref="ProblemResults"/>, so every error body is <c>application/problem+json</c>. No action carries
/// <c>[Consumes]</c>: the JSON input formatter is the only one registered, so MVC itself answers 415 for an absent or
/// non-JSON <c>Content-Type</c>.
/// </para>
/// <para>
/// Every problem title and detail is fixed English text. A detail names at most a room type and ISO dates, never a
/// guest id, an email address, a token, a price or exception text. Nothing is logged here.
/// </para>
/// <para>
/// <c>[ResponseCache(NoStore = true)]</c> gives every result an action returns, the 201, the 200 list, the 204 and
/// each problem it maps, exactly <c>Cache-Control: no-store</c>. Those responses describe one guest's own bookings
/// under URLs that every session shares, so no cache, a browser's private cache included, may store them. Responses
/// produced before an action runs, the JwtBearer 401 and MVC's 415 and binding 400, are left as the framework
/// writes them; none of them carries booking data.
/// </para>
/// </remarks>
/// <param name="bookings">The request-scoped booking service.</param>
[ApiController]
[Authorize]
[ResponseCache(NoStore = true)]
[Route("api/bookings")]
public sealed class BookingsController(BookingService bookings) : ControllerBase
{
    // Unlike the other asynchronous methods, these actions carry no Async suffix: each keeps the fixed name of the
    // endpoint operation it serves, the name MVC would report even with the suffix, since it trims a trailing Async.

    /// <summary>
    /// Books a room of a hotel's room type for a stay: <c>POST /api/bookings</c>. The server assigns the room and
    /// computes the total, and the booking is confirmed at once.
    /// </summary>
    /// <remarks>
    /// Checks run in this order, and the first failure decides the response:
    /// <list type="bullet">
    /// <item><description>
    /// 401, from JwtBearer before the body is bound, when the bearer token is missing, invalid or expired, or carries
    /// no valid guest id in <c>sub</c>.
    /// </description></item>
    /// <item><description>
    /// 415, from MVC, when <c>Content-Type</c> is absent or is not JSON, a bodyless request included.
    /// </description></item>
    /// <item><description>
    /// 400 <see cref="ValidationProblemDetails"/>, from MVC, for an empty, malformed or non-object JSON body, a field
    /// of the wrong JSON type, or a missing or non-positive <c>hotelId</c>, <c>roomTypeId</c> or <c>guests</c>.
    /// </description></item>
    /// <item><description>
    /// 400 keyed <c>checkIn</c> or <c>checkOut</c> when a date is missing or is not an exact <c>yyyy-MM-dd</c>
    /// calendar date, <c>checkOut</c> is not after <c>checkIn</c>, <c>checkIn</c> is before today (UTC), or the stay
    /// is longer than 30 nights.
    /// </description></item>
    /// <item><description>
    /// 404 when no hotel has <c>hotelId</c>, or when no room type of that hotel has <c>roomTypeId</c>.
    /// </description></item>
    /// <item><description>
    /// 400 keyed <c>guests</c> when the guest count exceeds the room type's capacity.
    /// </description></item>
    /// <item><description>
    /// 409 when no free room of the type is found for the stay, or a concurrent booking conflicts with the selected
    /// room.
    /// </description></item>
    /// <item><description>
    /// 201 with the confirmed booking, <c>{ id, hotelId, hotelName, roomTypeId, roomTypeName, roomNumber, checkIn,
    /// checkOut, nights, guests, totalPrice, status, createdAt }</c>, and <c>Cache-Control: no-store</c>. There is
    /// no <c>Location</c> header, because no endpoint reads a single booking.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="request">
    /// The body, already bound and validated by MVC. Its dates arrive as sent and are validated by the service.
    /// </param>
    /// <param name="cancellationToken">Cancels the database work when the request is aborted.</param>
    /// <returns>The 201 result, or the 400, 404 or 409 problem.</returns>
    /// <exception cref="UnreachableException">The service returned an unmapped outcome.</exception>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateBookingRequest request,
        CancellationToken cancellationToken)
    {
        CreateBookingResult result = await bookings.CreateAsync(User.GetGuestId(), request, cancellationToken);

        // The nested result types are qualified so that Created cannot resolve to ControllerBase.Created.
        return result switch
        {
            CreateBookingResult.Created created =>
                StatusCode(StatusCodes.Status201Created, created.Booking),
            CreateBookingResult.InvalidStay invalidStay =>
                this.BadRequestProblem(invalidStay.Errors),
            CreateBookingResult.HotelNotFound =>
                this.NotFoundProblem("Hotel not found", "No hotel matches the requested hotelId."),
            CreateBookingResult.RoomTypeNotFound =>
                this.NotFoundProblem(
                    "Room type not found",
                    "No room type with the requested roomTypeId belongs to this hotel."),
            CreateBookingResult.CapacityExceeded capacityExceeded =>
                this.BadRequestProblem(
                    new Dictionary<string, string[]>
                    {
                        ["guests"] = [CapacityMessage(capacityExceeded.MaxGuests)],
                    },
                    "The requested guest count exceeds the room type's capacity."),
            CreateBookingResult.NoRoomAvailable noRoomAvailable =>
                this.ConflictProblem(
                    "No room available",
                    $"No {noRoomAvailable.RoomTypeName} room is free from {FormatDate(noRoomAvailable.CheckIn)} "
                    + $"to {FormatDate(noRoomAvailable.CheckOut)}."),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// Lists the caller's own bookings, confirmed and cancelled, newest first: <c>GET /api/bookings</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// 200 with the caller's bookings, in the same shape as the 201 body of <see cref="Create"/>, ordered by
    /// <c>createdAt</c> and then <c>id</c>, both descending. The list is <c>[]</c> when the guest has no bookings.
    /// The response carries <c>Cache-Control: no-store</c>: every session reads its own list from this one URL, so a
    /// stored copy could be served to the next guest who signs in on the same browser.
    /// </description></item>
    /// <item><description>
    /// 401, from JwtBearer, when the bearer token is missing, invalid or expired, or carries no valid guest id in
    /// <c>sub</c>.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the database query when the request is aborted.</param>
    /// <returns>The 200 result with the caller's bookings.</returns>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        IReadOnlyList<BookingResponse> response = await bookings.ListAsync(User.GetGuestId(), cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Cancels one of the caller's own bookings, which frees its room for those dates again:
    /// <c>DELETE /api/bookings/{id}</c>.
    /// </summary>
    /// <remarks>
    /// Checks run in this order, and the first failure decides the response:
    /// <list type="bullet">
    /// <item><description>
    /// 401, from JwtBearer, when the bearer token is missing, invalid or expired, or carries no valid guest id in
    /// <c>sub</c>.
    /// </description></item>
    /// <item><description>
    /// 404 when no booking has <paramref name="id"/>. A non-integer id matches no route, and routing answers it with
    /// the same status.
    /// </description></item>
    /// <item><description>
    /// 403 when the booking belongs to another guest. Ownership is checked before the booking's state, so another
    /// guest's request never discloses whether the booking is confirmed, cancelled or already started. The 403 itself,
    /// unlike the 404 for an unknown id, does reveal that a booking with this id exists.
    /// </description></item>
    /// <item><description>
    /// 409 when the booking is already cancelled, or when its check-in date is before today (UTC). A stay that starts
    /// today can be cancelled until the end of today (UTC).
    /// </description></item>
    /// <item><description>
    /// 204 with no body: the booking's status is now <c>Cancelled</c>.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="id">The booking's id, from the route.</param>
    /// <param name="cancellationToken">Cancels the database work when the request is aborted.</param>
    /// <returns>The 204 result, or the 403, 404 or 409 problem.</returns>
    /// <exception cref="UnreachableException">The service returned an unmapped outcome.</exception>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Cancel(int id, CancellationToken cancellationToken)
    {
        CancelResult result = await bookings.CancelAsync(User.GetGuestId(), id, cancellationToken);

        // Qualified for the same reason as in Create: NotFound also names a ControllerBase method.
        return result switch
        {
            CancelResult.Cancelled => NoContent(),
            CancelResult.NotFound =>
                this.NotFoundProblem("Booking not found", "No booking matches the requested id."),
            CancelResult.Forbidden =>
                this.ForbiddenProblem(
                    "Booking belongs to another guest",
                    "This booking belongs to another guest and cannot be cancelled."),
            CancelResult.AlreadyCancelled =>
                this.ConflictProblem("Booking already cancelled", "This booking has already been cancelled."),
            CancelResult.CheckInPassed =>
                this.ConflictProblem(
                    "Check-in date has passed",
                    "Bookings can be cancelled only until the end of their check-in date (UTC)."),
            _ => throw new UnreachableException(),
        };
    }

    private static string FormatDate(DateOnly date)
    {
        return date.ToString(IsoDate.Format, CultureInfo.InvariantCulture);
    }

    // The capacity message states the room type's limit, never the requested count, and does not name its own
    // field, which the errors key identifies. The noun agrees with the limit, which can be 1.
    private static string CapacityMessage(int maxGuests)
    {
        return maxGuests == 1
            ? "This room type takes at most 1 guest."
            : string.Create(CultureInfo.InvariantCulture, $"This room type takes at most {maxGuests} guests.");
    }
}