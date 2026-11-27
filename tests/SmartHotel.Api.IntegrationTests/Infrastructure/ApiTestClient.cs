using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SmartHotel.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Request helpers for the API's endpoints, sent through one <see cref="HttpClient"/> of the in-process host.
/// The raw helpers return the response for the caller to assert on and dispose; the typed helpers assert the success
/// status and return the parsed body.
/// </summary>
/// <remarks>
/// <para>
/// The nested wire records are declared here, independently of the API's contract records, so the serialized shape
/// the API sends is what the tests verify: a renamed member or a changed format fails deserialization or an
/// assertion instead of changing on both sides at once.
/// </para>
/// <para>
/// One instance is safe for concurrent requests. Each request is its own <see cref="HttpRequestMessage"/> that
/// carries its own <c>Authorization</c> header, and <see cref="HttpClient.DefaultRequestHeaders"/> is never changed,
/// so concurrent requests with different tokens cannot see each other's headers.
/// </para>
/// <para>
/// No helper writes output, and no failure message contains a token or a password.
/// </para>
/// </remarks>
public sealed class ApiTestClient
{
    private const string RegisterRoute = "api/auth/register";
    private const string LoginRoute = "api/auth/login";
    private const string HotelsRoute = "api/hotels";
    private const string BookingsRoute = "api/bookings";

    private const string BearerScheme = "Bearer";
    private const string JsonMediaType = "application/json";
    private const string ProblemMediaType = "application/problem+json";
    private const string IsoDateFormat = "yyyy-MM-dd";

    // Fixed, non-secret values for throwaway guests in a per-class test database. The password meets the API's
    // 8-character minimum.
    private const string TestPassword = "Passw0rd!x";
    private const string TestFullName = "Test Guest";

    /// <summary>
    /// Wraps <paramref name="http"/>, whose base address is the host's root, so every route is relative.
    /// </summary>
    /// <param name="http">The client created by the test host. The caller owns it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> is <see langword="null"/>.</exception>
    public ApiTestClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        Http = http;
    }

    /// <summary>
    /// The underlying client, for requests the helpers do not build: a missing or unusual <c>Content-Type</c>, an
    /// empty or malformed body, another method such as <c>PUT</c>, or an explicit <c>Accept</c> header. Set headers
    /// on each <see cref="HttpRequestMessage"/>, never on <see cref="HttpClient.DefaultRequestHeaders"/>, because the
    /// client is shared by concurrent requests.
    /// </summary>
    public HttpClient Http { get; }

    /// <summary>
    /// The serializer options for request bodies and wire records: the Web defaults (camelCase names,
    /// case-insensitive reading, numbers in quotes accepted on read) plus string enums. The Web defaults never omit a
    /// <see langword="null"/> member, so a body can send an explicit JSON <c>null</c>.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The current UTC calendar date, the API's definition of "today": a check-in before it is in the past, and a
    /// booking whose check-in is before it can no longer be cancelled. It is computed on every read, so a run that
    /// crosses midnight UTC stays consistent with the server.
    /// </summary>
    public static DateOnly TodayUtc => DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

    /// <summary>
    /// The year after <see cref="TodayUtc"/>'s year. Its dates are always in the future, so date-format cases can
    /// pair the valid <c>Y-01-05</c> and <c>Y-01-07</c> with broken forms such as <c>Y-1-5</c>.
    /// </summary>
    public static int NextYear => TodayUtc.Year + 1;

    /// <summary>
    /// Formats <paramref name="date"/> as exactly <c>yyyy-MM-dd</c>, the only date form the API accepts.
    /// </summary>
    /// <param name="date">The date to format.</param>
    /// <returns>The date in invariant-culture <c>yyyy-MM-dd</c> form.</returns>
    public static string Iso(DateOnly date)
    {
        return date.ToString(IsoDateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds a <c>POST /api/bookings</c> body with the camelCase members <c>hotelId</c>, <c>roomTypeId</c>,
    /// <c>checkIn</c>, <c>checkOut</c> and <c>guests</c>. It has no price member, because the server computes the
    /// total. Tests can remove a member or give it a value of the wrong JSON type before sending it.
    /// </summary>
    /// <param name="hotelId">The hotel's id.</param>
    /// <param name="roomTypeId">The room type's id.</param>
    /// <param name="checkIn">The check-in date, written as <c>yyyy-MM-dd</c>.</param>
    /// <param name="checkOut">The check-out date, written as <c>yyyy-MM-dd</c>.</param>
    /// <param name="guests">The party size.</param>
    /// <returns>A new body, so concurrent requests never share one node.</returns>
    public static JsonObject BookingBody(int hotelId, int roomTypeId, DateOnly checkIn, DateOnly checkOut, int guests)
    {
        return new JsonObject
        {
            ["hotelId"] = hotelId,
            ["roomTypeId"] = roomTypeId,
            ["checkIn"] = Iso(checkIn),
            ["checkOut"] = Iso(checkOut),
            ["guests"] = guests,
        };
    }

    /// <summary>
    /// Sends <c>POST /api/auth/register</c> with <paramref name="body"/> serialized as JSON through
    /// <see cref="JsonOptions"/>, with <c>Content-Type: application/json; charset=utf-8</c>.
    /// </summary>
    /// <param name="body">The body, such as an anonymous object or a <see cref="JsonObject"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    public async Task<HttpResponseMessage> RegisterAsync(object body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await SendAsync(HttpMethod.Post, RegisterRoute, JsonBody(body), token: null, cancellationToken);
    }

    /// <summary>
    /// Sends <c>POST /api/auth/login</c> with <paramref name="body"/> serialized as JSON through
    /// <see cref="JsonOptions"/>, with <c>Content-Type: application/json; charset=utf-8</c>.
    /// </summary>
    /// <param name="body">
    /// The body, such as an anonymous object or a <see cref="JsonObject"/>. A <see langword="null"/> member is sent
    /// as an explicit JSON <c>null</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    public async Task<HttpResponseMessage> LoginAsync(object body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await SendAsync(HttpMethod.Post, LoginRoute, JsonBody(body), token: null, cancellationToken);
    }

    /// <summary>
    /// Sends <c>GET /api/hotels</c>. Each non-<see langword="null"/> argument becomes a query parameter, in the order
    /// <c>city</c>, <c>checkIn</c>, <c>checkOut</c>, <c>guests</c>, with its value escaped, so blank, slashed or
    /// over-long values arrive intact. A <see langword="null"/> argument omits its parameter.
    /// </summary>
    /// <param name="city">The <c>city</c> value, or <see langword="null"/> to omit it.</param>
    /// <param name="checkIn">The <c>checkIn</c> value, or <see langword="null"/> to omit it.</param>
    /// <param name="checkOut">The <c>checkOut</c> value, or <see langword="null"/> to omit it.</param>
    /// <param name="guests">The <c>guests</c> value, or <see langword="null"/> to omit it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller disposes.</returns>
    public async Task<HttpResponseMessage> SearchAsync(
        string? city,
        string? checkIn,
        string? checkOut,
        string? guests,
        CancellationToken cancellationToken)
    {
        string uri = WithQuery(
            HotelsRoute,
            ("city", city),
            ("checkIn", checkIn),
            ("checkOut", checkOut),
            ("guests", guests));

        return await SendAsync(HttpMethod.Get, uri, content: null, token: null, cancellationToken);
    }

    /// <summary>
    /// Sends <c>GET /api/hotels/{id}</c>, with the query built by the same rules as <see cref="SearchAsync"/>.
    /// </summary>
    /// <param name="id">The hotel's id.</param>
    /// <param name="checkIn">The <c>checkIn</c> value, or <see langword="null"/> to omit it.</param>
    /// <param name="checkOut">The <c>checkOut</c> value, or <see langword="null"/> to omit it.</param>
    /// <param name="guests">The <c>guests</c> value, or <see langword="null"/> to omit it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller disposes.</returns>
    public async Task<HttpResponseMessage> GetHotelAsync(
        int id,
        string? checkIn,
        string? checkOut,
        string? guests,
        CancellationToken cancellationToken)
    {
        string uri = WithQuery(
            $"{HotelsRoute}/{id.ToString(CultureInfo.InvariantCulture)}",
            ("checkIn", checkIn),
            ("checkOut", checkOut),
            ("guests", guests));

        return await SendAsync(HttpMethod.Get, uri, content: null, token: null, cancellationToken);
    }

    /// <summary>
    /// Sends <c>POST /api/bookings</c> with <paramref name="body"/> as UTF-8 JSON, with
    /// <c>Content-Type: application/json; charset=utf-8</c>.
    /// </summary>
    /// <param name="body">
    /// The body, usually from <see cref="BookingBody"/>. It is serialized before the request is sent, so the caller
    /// may change it as soon as this method returns.
    /// </param>
    /// <param name="token">The bearer token, or <see langword="null"/> to send no <c>Authorization</c> header.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    public async Task<HttpResponseMessage> CreateBookingAsync(
        JsonObject body,
        string? token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        StringContent content = new(body.ToJsonString(), Encoding.UTF8, JsonMediaType);
        return await SendAsync(HttpMethod.Post, BookingsRoute, content, token, cancellationToken);
    }

    /// <summary>Sends <c>GET /api/bookings</c>, the caller's own bookings.</summary>
    /// <param name="token">The bearer token, or <see langword="null"/> to send no <c>Authorization</c> header.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller disposes.</returns>
    public async Task<HttpResponseMessage> ListBookingsAsync(string? token, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Get, BookingsRoute, content: null, token, cancellationToken);
    }

    /// <summary>Sends <c>DELETE /api/bookings/{id}</c>, which cancels the booking.</summary>
    /// <param name="id">The booking's id.</param>
    /// <param name="token">The bearer token, or <see langword="null"/> to send no <c>Authorization</c> header.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller disposes.</returns>
    public async Task<HttpResponseMessage> CancelBookingAsync(
        int id,
        string? token,
        CancellationToken cancellationToken)
    {
        string uri = $"{BookingsRoute}/{id.ToString(CultureInfo.InvariantCulture)}";
        return await SendAsync(HttpMethod.Delete, uri, content: null, token, cancellationToken);
    }

    /// <summary>
    /// Deserializes the body of <paramref name="response"/> as <typeparamref name="T"/> through
    /// <see cref="JsonOptions"/>.
    /// </summary>
    /// <typeparam name="T">A wire record, or a list of them.</typeparam>
    /// <param name="response">The response to read. It is not disposed; the caller owns it.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns>The deserialized body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The body is the JSON literal <c>null</c>.</exception>
    /// <exception cref="JsonException">The body is not JSON of the shape <typeparamref name="T"/> declares.</exception>
    // An instance member although it reads no instance state (CA1822, info): tests call it on the client they send
    // requests with, which a static member would not allow.
    public async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return value ?? throw new InvalidOperationException(
            $"The response body deserialized to null instead of a {typeof(T).Name}.");
    }

    /// <summary>
    /// Registers a new guest with a unique email, the fixed test password and the name <c>Test Guest</c>, then logs
    /// in as that guest.
    /// </summary>
    /// <param name="cancellationToken">Cancels both requests.</param>
    /// <returns>The guest's id, credentials and bearer token.</returns>
    public async Task<GuestSession> RegisterGuestAsync(CancellationToken cancellationToken)
    {
        // A fresh GUID keeps the email unique within a class's shared database. It is already in the normalized
        // form the API stores: trimmed, lower case and printable ASCII.
        string email = $"guest+{Guid.NewGuid():N}@example.test";

        using HttpResponseMessage registered = await RegisterAsync(
            new { email, password = TestPassword, fullName = TestFullName },
            cancellationToken);
        await EnsureStatusAsync(registered, HttpStatusCode.Created, $"POST {RegisterRoute}", cancellationToken);
        RegisterResponse registration = await ReadAsync<RegisterResponse>(registered, cancellationToken);

        using HttpResponseMessage loggedIn = await LoginAsync(
            new { email, password = TestPassword },
            cancellationToken);
        await EnsureStatusAsync(loggedIn, HttpStatusCode.OK, $"POST {LoginRoute}", cancellationToken);
        LoginResponse login = await ReadAsync<LoginResponse>(loggedIn, cancellationToken);

        return new GuestSession(registration.GuestId, email, TestPassword, login.Token);
    }

    /// <summary>
    /// Reads <c>GET /api/hotels/{id}</c> for a stay, asserting 200.
    /// </summary>
    /// <param name="hotelId">The hotel's id.</param>
    /// <param name="checkIn">The check-in date.</param>
    /// <param name="checkOut">The check-out date.</param>
    /// <param name="guests">The party size.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The hotel with every room type's availability and stay total.</returns>
    public async Task<HotelDetail> GetHotelDetailAsync(
        int hotelId,
        DateOnly checkIn,
        DateOnly checkOut,
        int guests,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await GetHotelAsync(
            hotelId,
            Iso(checkIn),
            Iso(checkOut),
            guests.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        await EnsureStatusAsync(response, HttpStatusCode.OK, $"GET {HotelsRoute}/{{id}}", cancellationToken);

        return await ReadAsync<HotelDetail>(response, cancellationToken);
    }

    /// <summary>
    /// Sends <c>POST /api/bookings</c> with <paramref name="body"/>, asserting 201.
    /// </summary>
    /// <param name="body">The body, usually from <see cref="BookingBody"/>.</param>
    /// <param name="token">The booking guest's bearer token.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The created booking.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="body"/> or <paramref name="token"/> is <see langword="null"/>.
    /// </exception>
    public async Task<Booking> CreateBookingOkAsync(JsonObject body, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        using HttpResponseMessage response = await CreateBookingAsync(body, token, cancellationToken);
        await EnsureStatusAsync(response, HttpStatusCode.Created, $"POST {BookingsRoute}", cancellationToken);

        return await ReadAsync<Booking>(response, cancellationToken);
    }

    /// <summary>
    /// Books a room type for a stay with a body from <see cref="BookingBody"/>, asserting 201.
    /// </summary>
    /// <param name="token">The booking guest's bearer token.</param>
    /// <param name="hotelId">The hotel's id.</param>
    /// <param name="roomTypeId">The room type's id.</param>
    /// <param name="checkIn">The check-in date.</param>
    /// <param name="checkOut">The check-out date.</param>
    /// <param name="guests">The party size.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The created booking.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is <see langword="null"/>.</exception>
    public async Task<Booking> CreateBookingOkAsync(
        string token,
        int hotelId,
        int roomTypeId,
        DateOnly checkIn,
        DateOnly checkOut,
        int guests,
        CancellationToken cancellationToken)
    {
        return await CreateBookingOkAsync(
            BookingBody(hotelId, roomTypeId, checkIn, checkOut, guests),
            token,
            cancellationToken);
    }

    private static JsonContent JsonBody(object body)
    {
        return JsonContent.Create(body, options: JsonOptions);
    }

    // Appends each pair whose value is not null, in the given order. Uri.EscapeDataString escapes everything outside
    // the unreserved set, so a blank value arrives as spaces, a slash as a slash and a plus as a plus.
    private static string WithQuery(string path, params (string Name, string? Value)[] parameters)
    {
        StringBuilder uri = new(path);
        char separator = '?';
        foreach ((string name, string? value) in parameters)
        {
            if (value is null)
            {
                continue;
            }

            uri.Append(separator).Append(name).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return uri.ToString();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string uri,
        HttpContent? content,
        string? token,
        CancellationToken cancellationToken)
    {
        // Disposing the request also disposes its content; the response outlives both.
        using HttpRequestMessage request = new(method, uri) { Content = content };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, token);
        }

        return await Http.SendAsync(request, cancellationToken);
    }

    // The failure message quotes the body only when it is a problem document. The API's problem bodies never carry a
    // token, password or email, while a success body can: a login response holds a bearer token.
    private static async Task EnsureStatusAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        string body = string.Equals(mediaType, ProblemMediaType, StringComparison.OrdinalIgnoreCase)
            ? await response.Content.ReadAsStringAsync(cancellationToken)
            : $"(the body is not {ProblemMediaType}, so it is not quoted)";

        Assert.Fail($"Expected {operation} to return {(int)expected} but got {(int)response.StatusCode}: {body}");
    }

    /// <summary>The 201 body of <c>POST /api/auth/register</c>: <c>{ "guestId": n }</c>.</summary>
    /// <param name="GuestId">The new guest's id.</param>
    public sealed record RegisterResponse(int GuestId);

    /// <summary>
    /// The 200 body of <c>POST /api/auth/login</c>:
    /// <c>{ "token": "&lt;jwt&gt;", "expiresAt": "yyyy-MM-ddTHH:mm:ssZ" }</c>.
    /// </summary>
    /// <param name="Token">The signed bearer token.</param>
    /// <param name="ExpiresAt">The token's expiry instant, read as a UTC <see cref="DateTime"/>.</param>
    public sealed record LoginResponse(string Token, DateTime ExpiresAt)
    {
        /// <summary>Returns the expiry with the token redacted.</summary>
        /// <returns>A description that carries no token.</returns>
        public override string ToString()
        {
            // The synthesized record ToString would print the bearer token into assertion messages.
            string expiresAt = ExpiresAt.ToString("O", CultureInfo.InvariantCulture);
            return $"LoginResponse {{ Token = [redacted], ExpiresAt = {expiresAt} }}";
        }
    }

    /// <summary>
    /// One element of the <c>GET /api/hotels</c> array: <c>{ id, name, city, address, lowestNightlyPrice }</c>.
    /// </summary>
    /// <param name="Id">The hotel's id.</param>
    /// <param name="Name">The hotel's display name.</param>
    /// <param name="City">The city as stored, whatever the letter case of the search.</param>
    /// <param name="Address">The hotel's street address.</param>
    /// <param name="LowestNightlyPrice">
    /// The lowest nightly price in USD among the room types that can host the party and have a free room.
    /// </param>
    public sealed record HotelSummary(int Id, string Name, string City, string Address, decimal LowestNightlyPrice);

    /// <summary>
    /// The 200 body of <c>GET /api/hotels/{id}</c>:
    /// <c>{ id, name, city, address, description, checkIn, checkOut, nights, guests, roomTypes }</c>.
    /// </summary>
    /// <param name="Id">The hotel's id.</param>
    /// <param name="Name">The hotel's display name.</param>
    /// <param name="City">The city as stored.</param>
    /// <param name="Address">The hotel's street address.</param>
    /// <param name="Description">The hotel's description.</param>
    /// <param name="CheckIn">The echoed check-in date, read from <c>yyyy-MM-dd</c>.</param>
    /// <param name="CheckOut">The echoed check-out date, read from <c>yyyy-MM-dd</c>.</param>
    /// <param name="Nights">The number of nights in the stay.</param>
    /// <param name="Guests">The echoed party size, 1 when the query omitted it.</param>
    /// <param name="RoomTypes">Every room type of the hotel, ordered by nightly price.</param>
    public sealed record HotelDetail(
        int Id,
        string Name,
        string City,
        string Address,
        string Description,
        DateOnly CheckIn,
        DateOnly CheckOut,
        int Nights,
        int Guests,
        IReadOnlyList<RoomTypeAvailability> RoomTypes);

    /// <summary>
    /// One element of <see cref="HotelDetail.RoomTypes"/>:
    /// <c>{ id, name, maxGuests, nightlyPrice, availableRooms, stayTotal }</c>.
    /// </summary>
    /// <param name="Id">The room type's id.</param>
    /// <param name="Name">The room type's display name, such as <c>Standard</c>.</param>
    /// <param name="MaxGuests">The largest party one room of this type can host.</param>
    /// <param name="NightlyPrice">The price per night in USD.</param>
    /// <param name="AvailableRooms">
    /// The rooms of this type with no confirmed booking overlapping the stay, whatever the party size.
    /// </param>
    /// <param name="StayTotal">The price of the whole stay in USD.</param>
    public sealed record RoomTypeAvailability(
        int Id,
        string Name,
        int MaxGuests,
        decimal NightlyPrice,
        int AvailableRooms,
        decimal StayTotal);

    /// <summary>
    /// A booking as the API returns it: the 201 body of <c>POST /api/bookings</c> and each element of
    /// <c>GET /api/bookings</c>.
    /// </summary>
    /// <param name="Id">The booking's id.</param>
    /// <param name="HotelId">The hotel's id.</param>
    /// <param name="HotelName">The hotel's display name.</param>
    /// <param name="RoomTypeId">The booked room type's id.</param>
    /// <param name="RoomTypeName">The booked room type's display name.</param>
    /// <param name="RoomNumber">The room the server assigned.</param>
    /// <param name="CheckIn">The check-in date, read from <c>yyyy-MM-dd</c>.</param>
    /// <param name="CheckOut">The check-out date, read from <c>yyyy-MM-dd</c>.</param>
    /// <param name="Nights">The number of nights.</param>
    /// <param name="Guests">The party size.</param>
    /// <param name="TotalPrice">The server-computed total in USD.</param>
    /// <param name="Status">
    /// The status exactly as serialized, <c>Confirmed</c> or <c>Cancelled</c>. It is a string rather than an enum so
    /// the API's string-enum serialization is what the tests compare.
    /// </param>
    /// <param name="CreatedAt">When the booking was created, read as a UTC <see cref="DateTime"/>.</param>
    public sealed record Booking(
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
        string Status,
        DateTime CreatedAt);

    /// <summary>A registered and logged-in test guest, from <see cref="RegisterGuestAsync"/>.</summary>
    /// <param name="GuestId">The guest's id, the token's <c>sub</c>.</param>
    /// <param name="Email">The registered email, already in normalized form.</param>
    /// <param name="Password">The password the guest registered with.</param>
    /// <param name="Token">A bearer token for the guest, valid for 60 minutes.</param>
    public sealed record GuestSession(int GuestId, string Email, string Password, string Token)
    {
        /// <summary>Returns the guest id with the email, password and token redacted.</summary>
        /// <returns>A description that carries no credential.</returns>
        public override string ToString()
        {
            // The synthesized record ToString would print the credentials into assertion messages.
            return $"GuestSession {{ GuestId = {GuestId.ToString(CultureInfo.InvariantCulture)}, Email = [redacted], "
                + "Password = [redacted], Token = [redacted] }";
        }
    }
}