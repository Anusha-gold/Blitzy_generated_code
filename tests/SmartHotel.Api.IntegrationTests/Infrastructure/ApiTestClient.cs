using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Xunit.Sdk;

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
/// assertion instead of changing on both sides at once. <see cref="ReadAsync{T}"/> checks a success body against
/// that shape on the raw JSON before it binds: the media type, the exact camelCase member set and each member's
/// JSON kind and format.
/// </para>
/// <para>
/// One instance is safe for concurrent requests. Each request is its own <see cref="HttpRequestMessage"/> that
/// carries its own <c>Authorization</c> header, and <see cref="HttpClient.DefaultRequestHeaders"/> is never changed,
/// so concurrent requests with different tokens cannot see each other's headers.
/// </para>
/// <para>
/// No helper writes output. Failure messages carry only the operation, the numeric status codes, the name of the
/// expected wire type and the members and JSON path it declares. They never quote a body, a member name taken from
/// it, a header, a parser message, a token, a password or an email, because a response can carry any of them and
/// test output is logged.
/// </para>
/// </remarks>
public sealed partial class ApiTestClient
{
    private const string RegisterRoute = "api/auth/register";
    private const string LoginRoute = "api/auth/login";
    private const string HotelsRoute = "api/hotels";
    private const string BookingsRoute = "api/bookings";

    private const string BearerScheme = "Bearer";
    private const string JsonMediaType = "application/json";
    private const string Utf8CharSet = "utf-8";
    private const string CharSetParameter = "charset";
    private const string IsoDateFormat = "yyyy-MM-dd";
    private const string CacheControlHeader = "Cache-Control";
    private const string NoStoreDirective = "no-store";

    // The JSON path of a response body's root, which every failure message's path starts from.
    private const string RootPath = "$";

    // Fixed, non-secret values for throwaway guests in a per-class test database. The password meets the API's
    // 8-character minimum.
    private const string TestPassword = "Passw0rd!x";
    private const string TestFullName = "Test Guest";

    // The settings ReadAsync binds a success body with once its raw JSON has passed the wire-shape check. Unlike the
    // Web defaults of JsonOptions, they match member names case-sensitively, accept a number only as a JSON number
    // and reject a member the wire record does not declare, so binding cannot silently repair a body the SPA would
    // misread. Statuses stay strings through the enum converter, as the API writes them.
    private static readonly JsonSerializerOptions StrictReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

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
    /// The serializer options for request bodies: the Web defaults (camelCase names) plus string enums. The Web
    /// defaults never omit a <see langword="null"/> member, so a body can send an explicit JSON <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Their reading side is tolerant: it ignores letter case and unknown members and accepts numbers in quotes. No
    /// helper reads a response with them; <see cref="ReadAsync{T}"/> checks the raw body and then binds with strict
    /// settings instead.
    /// </remarks>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The current UTC calendar date, the API's definition of "today": a check-in before it is in the past, and a
    /// booking whose check-in is before it can no longer be cancelled. Every read takes a new reading of the system
    /// clock, so a scenario reads it once and derives from that one value every date it sends and, through
    /// <see cref="NextYear(DateOnly)"/>, its format-case year. Separate reads on either side of midnight UTC would
    /// otherwise build a stay of a different length, or a different year.
    /// </summary>
    public static DateOnly TodayUtc => DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

    /// <summary>
    /// The calendar year after <paramref name="today"/>'s year. Its dates are always in the future, so date-format
    /// cases can pair the valid <c>Y-01-05</c> and <c>Y-01-07</c> with broken forms such as <c>Y-1-5</c>.
    /// </summary>
    /// <param name="today">The scenario's one <see cref="TodayUtc"/> snapshot.</param>
    /// <returns>The year that follows the year of <paramref name="today"/>.</returns>
    public static int NextYear(DateOnly today)
    {
        return today.Year + 1;
    }

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
    /// Reads the success body of <paramref name="response"/> as <typeparamref name="T"/>, after checking it against
    /// the wire contract the SPA relies on: JSON, UTF-8, camelCase members, dates exactly <c>yyyy-MM-dd</c>,
    /// timestamps in UTC with a <c>Z</c> suffix and money as a JSON number with two decimals.
    /// </summary>
    /// <remarks>
    /// <para>The checks run in this order, and the first failure fails the test:</para>
    /// <list type="number">
    /// <item><description>
    /// <c>Content-Type</c> has the media type <c>application/json</c>, compared case-insensitively, and either no
    /// <c>charset</c> parameter or exactly one whose value is <c>utf-8</c> in any letter case, written as a token or
    /// as a quoted-string, which is compared without its quotes and with each quoted-pair unescaped. Any other
    /// charset name fails, the registered alias <c>csUTF8</c> and the unregistered spelling <c>utf8</c> included, and
    /// so does a malformed declaration: a <c>charset</c> without a value, an unterminated quoted-string or a repeated
    /// <c>charset</c> parameter.
    /// </description></item>
    /// <item><description>
    /// The body is well-formed JSON whose shape matches the one <typeparamref name="T"/> declares, checked on the raw
    /// JSON independently of binding. A wire record is a JSON object whose members are exactly its constructor
    /// parameters named in camelCase, each once, compared ordinally, so a missing, extra, repeated or
    /// differently-cased member fails. An <see cref="int"/> member is a JSON number with an integral
    /// <see cref="int"/> value; a <see cref="decimal"/> member is a JSON number, never a string, written with exactly
    /// two decimal places; a <see cref="string"/> member is a JSON string, never <c>null</c>; a
    /// <see cref="DateOnly"/> member is a string of exactly <c>yyyy-MM-dd</c> that names a real date; a
    /// <see cref="DateTime"/> member is a UTC ISO 8601 string ending in <c>Z</c>; and a <see cref="List{T}"/> or
    /// <see cref="IReadOnlyList{T}"/>, at the top level or as a member, is a JSON array whose every element is checked
    /// the same way.
    /// </description></item>
    /// <item><description>
    /// The body then binds with strict settings: case-sensitive camelCase names, numbers only as JSON numbers, no
    /// member the record does not declare, and statuses as strings.
    /// </description></item>
    /// </list>
    /// <para>
    /// Failure messages name only <typeparamref name="T"/> and the expected member's JSON path, such as
    /// <c>$[1].totalPrice</c>. They never quote the body, a member name taken from it, a header value or a parser
    /// message, because a response can carry a token or an email and test output is logged.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">
    /// A nested wire record such as <see cref="Booking"/>, or a <see cref="List{T}"/> or
    /// <see cref="IReadOnlyList{T}"/> of one.
    /// </typeparam>
    /// <param name="response">The response to read. It is not disposed; the caller owns it.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns>The body, bound to <typeparamref name="T"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/>, or a member type it declares, has no wire shape: it is none of the scalar types
    /// above, a list of one, or a record whose single public constructor declares its members.
    /// </exception>
    /// <exception cref="FailException">
    /// The media type or charset is wrong, the body is not well-formed JSON, it does not match the shape
    /// <typeparamref name="T"/> declares, or it does not bind.
    /// </exception>
    // An instance member although it reads no instance state (CA1822, info): tests call it on the client they send
    // requests with, which a static member would not allow.
    public async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        string wireType = WireTypeName(typeof(T));

        // Media types and charset names are case-insensitive (RFC 9110, sections 8.3.1 and 8.3.2).
        MediaTypeHeaderValue? contentType = response.Content.Headers.ContentType;
        bool isJson = string.Equals(contentType?.MediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase);
        bool isUtf8 = contentType is not null && HasUtf8OrNoCharSet(contentType);
        if (!isJson || !isUtf8)
        {
            throw FailException.ForFailure(
                $"Expected the {wireType} response to have Content-Type {JsonMediaType} with no charset or "
                + $"{Utf8CharSet} but "
                + (contentType is null ? "it has no valid Content-Type." : "it has another media type or charset."));
        }

        // The charset is UTF-8 or absent, and JSON without one is UTF-8 (RFC 8259, section 8.1), so the bytes are
        // parsed as they arrived.
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // The parser's message quotes characters of the body, so neither it nor the exception is passed on.
            throw FailException.ForFailure($"Expected the {wireType} response body to be well-formed JSON.");
        }

        using (document)
        {
            CheckWireValue(document.RootElement, typeof(T), RootPath, wireType);

            T? value;
            try
            {
                value = document.RootElement.Deserialize<T>(StrictReadOptions);
            }
            catch (JsonException)
            {
                // As above, the serializer's message names the path and can quote the body.
                throw FailException.ForFailure(
                    $"Expected the {wireType} response body to bind to its wire record with strict settings.");
            }

            return value ?? throw FailException.ForFailure(
                $"Expected the {wireType} response body to bind to a value rather than to null.");
        }
    }

    /// <summary>
    /// Asserts that <paramref name="response"/> carries exactly one <c>Cache-Control</c> header whose whole value is
    /// <c>no-store</c>, the policy for a response that holds one guest's own data.
    /// </summary>
    /// <param name="response">The response to check. It is not disposed; the caller owns it.</param>
    /// <param name="operation">
    /// A fixed label for the request, such as <c>GET api/bookings</c>, which the failure message names.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="response"/> or <paramref name="operation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="FailException">
    /// The header is missing, repeated or has any other value. The message never quotes the header.
    /// </exception>
    public static void AssertNoStore(HttpResponseMessage response, string operation)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(operation);

        // The values are compared as received, before any parsing, so a directive added beside no-store, such as
        // max-age or no-cache, or a second Cache-Control line also fails.
        HttpHeadersNonValidated headers = response.Headers.NonValidated;
        string[] received = headers.TryGetValues(CacheControlHeader, out HeaderStringValues values) ? [.. values] : [];
        Assert.True(
            received.Length == 1 && string.Equals(received[0], NoStoreDirective, StringComparison.Ordinal),
            $"Expected {operation} to answer with exactly {CacheControlHeader}: {NoStoreDirective}.");
    }

    /// <summary>
    /// Registers a new guest with a unique email, the fixed test password and the name <c>Test Guest</c>, then logs
    /// in as that guest.
    /// </summary>
    /// <param name="cancellationToken">Cancels both requests.</param>
    /// <returns>The guest's id, credentials and bearer token.</returns>
    internal async Task<GuestSession> RegisterGuestAsync(CancellationToken cancellationToken)
    {
        // A fresh GUID keeps the email unique within a class's shared database. It is already in the normalized
        // form the API stores: trimmed, lower case and printable ASCII.
        string email = $"guest+{Guid.NewGuid():N}@example.test";

        using HttpResponseMessage registered = await RegisterAsync(
            new { email, password = TestPassword, fullName = TestFullName },
            cancellationToken);
        EnsureStatus(registered, HttpStatusCode.Created, $"POST {RegisterRoute}");
        RegisterResponse registration = await ReadAsync<RegisterResponse>(registered, cancellationToken);

        using HttpResponseMessage loggedIn = await LoginAsync(
            new { email, password = TestPassword },
            cancellationToken);
        EnsureStatus(loggedIn, HttpStatusCode.OK, $"POST {LoginRoute}");
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
    internal async Task<HotelDetail> GetHotelDetailAsync(
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
        EnsureStatus(response, HttpStatusCode.OK, $"GET {HotelsRoute}/{{id}}");

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
    internal async Task<Booking> CreateBookingOkAsync(
        JsonObject body,
        string token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        using HttpResponseMessage response = await CreateBookingAsync(body, token, cancellationToken);
        EnsureStatus(response, HttpStatusCode.Created, $"POST {BookingsRoute}");

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
    internal async Task<Booking> CreateBookingOkAsync(
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

    // The failure message carries only the fixed operation label and the two numeric status codes. The body and the
    // headers are never read: any response, a problem document included, can echo a token, a password or an email
    // when the API regresses, and test output is logged. Tests that expect an error check it through ProblemAssert.
    private static void EnsureStatus(HttpResponseMessage response, HttpStatusCode expected, string operation)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        Assert.Fail($"Expected {operation} to return {(int)expected} but got {(int)response.StatusCode}.");
    }

    // True when the media type declares no charset, or exactly one whose value names UTF-8. A parameter value is a
    // token or a quoted-string (RFC 9110, section 5.6.6), so charset=utf-8 and charset="UTF-8" declare the same
    // encoding (section 8.3.1), but MediaTypeHeaderValue keeps a quoted-string as written, quotes included, so the
    // value is unquoted before the case-insensitive comparison. A charset without a value, a quoted-string that does
    // not close at the value's end and a repeated charset parameter (RFC 6838, section 4.3) are malformed and fail.
    // Only the registered name is accepted, because it is what the API writes: the alias csUTF8 and the unregistered
    // spelling utf8 fail as well.
    private static bool HasUtf8OrNoCharSet(MediaTypeHeaderValue contentType)
    {
        NameValueHeaderValue[] charSets =
        [
            .. contentType.Parameters.Where(
                parameter => string.Equals(parameter.Name, CharSetParameter, StringComparison.OrdinalIgnoreCase)),
        ];
        if (charSets.Length == 0)
        {
            return true;
        }

        return charSets.Length == 1
            && charSets[0].Value is { } value
            && TryUnquoteParameterValue(value, out string? name)
            && string.Equals(name, Utf8CharSet, StringComparison.OrdinalIgnoreCase);
    }

    // Reads a parameter value as its text: a token is returned unchanged, and a quoted-string (RFC 9110, section
    // 5.6.4) loses its surrounding quotes and has each quoted-pair replaced by the character after the backslash.
    // Fails for an empty value, and for a quoted-string whose closing quote is missing or is not the last character.
    private static bool TryUnquoteParameterValue(string value, [NotNullWhen(true)] out string? text)
    {
        text = null;
        if (value.Length == 0)
        {
            return false;
        }

        if (value[0] != '"')
        {
            text = value;
            return true;
        }

        StringBuilder unquoted = new(value.Length);
        for (int index = 1; index < value.Length; index++)
        {
            char current = value[index];
            if (current == '"')
            {
                if (index != value.Length - 1)
                {
                    return false;
                }

                text = unquoted.ToString();
                return true;
            }

            if (current == '\\')
            {
                index++;
                if (index == value.Length)
                {
                    return false;
                }

                current = value[index];
            }

            unquoted.Append(current);
        }

        return false;
    }

    // The name failure messages give a wire type: a record's own name, or "list of" followed by its element's name.
    private static string WireTypeName(Type type)
    {
        return TryGetListElementType(type, out Type? elementType)
            ? $"list of {WireTypeName(elementType)}"
            : type.Name;
    }

    // A list on the wire is a JSON array; List<T> and IReadOnlyList<T> are the forms the wire records and tests use.
    private static bool TryGetListElementType(Type type, [NotNullWhen(true)] out Type? elementType)
    {
        if (type.IsGenericType
            && (type.GetGenericTypeDefinition() == typeof(List<>)
                || type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        elementType = null;
        return false;
    }

    // Checks one raw JSON value against the form the wire contract gives its declared type, recursing into arrays and
    // objects. The path names only expected members and array positions, never anything read from the body.
    private static void CheckWireValue(JsonElement element, Type declaredType, string path, string wireType)
    {
        if (declaredType == typeof(int))
        {
            RequireWireForm(
                element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out _),
                wireType,
                path,
                "a JSON number with an integral Int32 value");
        }
        else if (declaredType == typeof(decimal))
        {
            // The raw text is checked because a decimal binds 129, 129.0 and 1.29e2 alike, while the contract
            // writes money with exactly two decimals.
            RequireWireForm(
                element.ValueKind == JsonValueKind.Number
                    && MoneyPattern().IsMatch(element.GetRawText())
                    && element.TryGetDecimal(out _),
                wireType,
                path,
                "a JSON number written with exactly two decimal places");
        }
        else if (declaredType == typeof(string))
        {
            RequireWireForm(element.ValueKind == JsonValueKind.String, wireType, path, "a JSON string");
        }
        else if (declaredType == typeof(DateOnly))
        {
            RequireWireForm(
                element.ValueKind == JsonValueKind.String
                    && element.GetString() is string date
                    && IsoDatePattern().IsMatch(date)
                    && DateOnly.TryParseExact(
                        date,
                        IsoDateFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _),
                wireType,
                path,
                "a JSON string of exactly yyyy-MM-dd that names a real date");
        }
        else if (declaredType == typeof(DateTime))
        {
            RequireWireForm(
                element.ValueKind == JsonValueKind.String
                    && element.GetString() is string instant
                    && UtcTimestampPattern().IsMatch(instant)
                    && DateTime.TryParse(
                        instant,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out DateTime parsed)
                    && parsed.Kind == DateTimeKind.Utc,
                wireType,
                path,
                "a JSON string holding a real UTC ISO 8601 timestamp ending in Z");
        }
        else if (TryGetListElementType(declaredType, out Type? elementType))
        {
            RequireWireForm(element.ValueKind == JsonValueKind.Array, wireType, path, "a JSON array");

            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                CheckWireValue(
                    item,
                    elementType,
                    string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"),
                    wireType);
                index++;
            }
        }
        else
        {
            CheckWireObject(element, declaredType, path, wireType);
        }
    }

    // A wire record is a JSON object with exactly the members its single public constructor declares, named in
    // camelCase as the API's Web JSON options write them. Ordinal comparison makes letter case significant.
    private static void CheckWireObject(JsonElement element, Type recordType, string path, string wireType)
    {
        ConstructorInfo[] constructors = recordType.GetConstructors();
        if (!recordType.IsClass
            || recordType.IsAbstract
            || constructors.Length != 1
            || constructors[0].GetParameters().Length == 0)
        {
            throw new NotSupportedException(
                $"{recordType.Name} has no wire shape: it is not a supported scalar, a list or a record whose single "
                + "public constructor declares its members.");
        }

        List<(string Name, Type Type)> declared = [];
        foreach (ParameterInfo parameter in constructors[0].GetParameters())
        {
            string parameterName = parameter.Name ?? throw new NotSupportedException(
                $"{recordType.Name} has no wire shape: a constructor parameter has no name.");
            declared.Add((JsonNamingPolicy.CamelCase.ConvertName(parameterName), parameter.ParameterType));
        }

        (string Name, Type Type)[] members = [.. declared];
        string expectedMembers = string.Join(", ", members.Select(static member => member.Name));

        RequireWireForm(element.ValueKind == JsonValueKind.Object, wireType, path, "a JSON object");

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            int position = Array.FindIndex(
                members,
                member => string.Equals(member.Name, property.Name, StringComparison.Ordinal));
            RequireWireForm(
                position >= 0 && seen.Add(property.Name),
                wireType,
                path,
                $"an object with exactly the members {expectedMembers}, each once, but it has another member or "
                + "repeats one");

            // The name is one of the expected members here, so the path it extends still quotes nothing of the body.
            (string name, Type type) = members[position];
            CheckWireValue(property.Value, type, $"{path}.{name}", wireType);
        }

        foreach ((string name, _) in members)
        {
            RequireWireForm(seen.Contains(name), wireType, path, $"an object with the member {name}");
        }
    }

    private static void RequireWireForm(bool condition, string wireType, string path, string expectation)
    {
        if (!condition)
        {
            throw FailException.ForFailure(
                $"Expected the {wireType} response body at {path} to be {expectation}.");
        }
    }

    // Money as the contract writes it: an optional minus sign, an integer part without leading zeros, and exactly two
    // decimal places, with no exponent.
    [GeneratedRegex(@"\A-?(?:0|[1-9][0-9]*)\.[0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MoneyPattern();

    // The exact yyyy-MM-dd form with ASCII digits; whether it is a real date is checked by parsing.
    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDatePattern();

    // ISO 8601 in UTC as System.Text.Json writes a UTC DateTime: whole seconds, then up to seven fractional digits,
    // then Z. An offset such as +00:00 or a missing designator fails.
    [GeneratedRegex(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?Z\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex UtcTimestampPattern();

    /// <summary>The 201 body of <c>POST /api/auth/register</c>: <c>{ "guestId": n }</c>.</summary>
    /// <param name="GuestId">The new guest's id.</param>
    internal sealed record RegisterResponse(int GuestId);

    /// <summary>
    /// The 200 body of <c>POST /api/auth/login</c>:
    /// <c>{ "token": "&lt;jwt&gt;", "expiresAt": "yyyy-MM-ddTHH:mm:ssZ" }</c>.
    /// </summary>
    /// <param name="Token">The signed bearer token.</param>
    /// <param name="ExpiresAt">The token's expiry instant, read as a UTC <see cref="DateTime"/>.</param>
    internal sealed record LoginResponse(string Token, DateTime ExpiresAt)
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
    internal sealed record HotelSummary(int Id, string Name, string City, string Address, decimal LowestNightlyPrice);

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
    internal sealed record HotelDetail(
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
    internal sealed record RoomTypeAvailability(
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
    internal sealed record Booking(
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
    internal sealed record GuestSession(int GuestId, string Email, string Password, string Token)
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