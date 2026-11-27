using System.Buffers.Binary;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Api.Modules.Auth;
using SmartHotel.Data;
using SmartHotel.Data.Entities;

using Xunit.Sdk;

namespace SmartHotel.Api.IntegrationTests.Auth;

public sealed class RegisterTests(SmartHotelApiFactory factory) : IClassFixture<SmartHotelApiFactory>
{
    private const string RegisterRoute = "api/auth/register";
    private const string JsonMediaType = "application/json";

    // Fixed, non-secret values for throwaway guests in this class's own test database.
    private const string ValidPassword = "Passw0rd!x";
    private const string ValidFullName = "Test Guest";

    // U+1F600, a character outside the Basic Multilingual Plane: one Unicode scalar value but two UTF-16 code units,
    // so a password built from it meets the registration bounds only when they count characters.
    private const string SupplementaryCharacter = "\U0001F600";

    // ASP.NET Core Identity's V3 hash layout: a 0x01 marker, then three big-endian uint32 values (PRF, iteration
    // count, salt length), then the salt and the subkey. The expected values are the ones AuthModule configures.
    private const int V3HashLength = 1 + 4 + 4 + 4 + 16 + 32;
    private const byte V3FormatMarker = 0x01;
    private const uint HmacSha512Prf = 2;
    private const uint ConfiguredIterationCount = 220_000;
    private const uint SaltLengthBytes = 16;

    [Theory]
    [InlineData("email-not-an-address", "email")]
    [InlineData("email-non-ascii", "email")]
    [InlineData("password-too-short", "password")]
    [InlineData("password-supplementary-too-short", "password")]
    [InlineData("password-supplementary-too-long", "password")]
    [InlineData("fullname-missing", "fullName")]
    [InlineData("empty-json-body", "$")]
    [InlineData("malformed-json", null)]
    public async Task Register_InvalidInput_Returns400(string caseLabel, string? expectedErrorKey)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();

        using HttpResponseMessage response = await SendInvalidRegistrationAsync(client, caseLabel, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedErrorKey, ct);
    }

    // The password bound is an attribute check like Required, so it is reported in the same 400 as a missing
    // fullName, and the register page can show both field errors at once.
    [Fact]
    public async Task Register_ShortPasswordAndMissingFullName_Returns400WithBothKeys()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();

        using HttpResponseMessage response = await client.RegisterAsync(
            new { email = UniqueEmail("twofields"), password = "short" },
            ct);

        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "password",
            ct);

        // ProblemAssert has checked that errors is an object of message-string arrays.
        Assert.True(
            problem.GetProperty("errors").TryGetProperty("fullName", out JsonElement fullNameMessages)
                && fullNameMessages.GetArrayLength() > 0,
            "Expected errors to contain fullName, with at least one message, as well as password.");
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        string email = UniqueEmail("dup");

        using HttpResponseMessage first = await client.RegisterAsync(
            new { email, password = ValidPassword, fullName = ValidFullName },
            ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Only the letter case differs, so the 409 shows that uniqueness holds on the normalized form.
        using HttpResponseMessage second = await client.RegisterAsync(
            new { email = email.ToUpperInvariant(), password = ValidPassword, fullName = ValidFullName },
            ct);

        await ProblemAssert.AssertProblemAsync(second, HttpStatusCode.Conflict, cancellationToken: ct);
    }

    // A holder locks Guests against inserts before any request starts. Seeing every request queued at its INSERT
    // proves that each one passed the email pre-check, so every loser is refused by UX_Guests_Email's 23505, the race
    // path, and none by the pre-check.
    [Fact]
    public async Task Register_ConcurrentDuplicateEmail_ReturnsOneCreatedAndConflictsWithoutErrorRecords()
    {
        const int raceSize = 10;

        // AuthService's GuestRegistered.
        const int guestRegisteredEventId = 1001;

        // SHARE mode conflicts with the ROW EXCLUSIVE lock an INSERT takes and with nothing a SELECT takes, so every
        // registration passes its email pre-check and then queues at its insert behind the holder.
        const string lockGuestsSql = "LOCK TABLE \"Guests\" IN SHARE MODE";

        TimeSpan observationLimit = TimeSpan.FromSeconds(30);
        TimeSpan drainLimit = TimeSpan.FromSeconds(30);
        string authServiceCategory = typeof(AuthService).FullName!;

        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.LogCapture capture = new();
        await using SmartHotelApiFactory variant = factory.CreateLogCaptureVariant(
            capture,
            SmartHotelApiFactory.LogCapture.DataLayerRules,
            additionalOverrides: null);

        // A hand-built factory is not a fixture, so it is started here.
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();
        string email = UniqueEmail("race");

        await using NpgsqlConnection holder = new(variant.DatabaseConnectionString);
        await holder.OpenAsync(ct);
        await using NpgsqlTransaction holderTx = await holder.BeginTransactionAsync(ct);
        await using (NpgsqlCommand lockGuests = new(lockGuestsSql, holder, holderTx))
        {
            await lockGuests.ExecuteNonQueryAsync(ct);
        }

        int mark = capture.Count;

        // ToArray starts every request at once, each with the same address.
        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, raceSize)
            .Select(_ => client.RegisterAsync(new { email, password = ValidPassword, fullName = ValidFullName }, ct))
            .ToArray();

        long waiting = -1;
        HttpResponseMessage[] responses;
        try
        {
            waiting = await WaitForInsertWaitersAsync(
                variant.DatabaseConnectionString,
                holder.ProcessID,
                raceSize,
                observationLimit,
                ct);
        }
        finally
        {
            // Runs on every path. The holder changed nothing, so the rollback only releases the lock, even while the
            // test is being cancelled, and every request is drained before the variant host is disposed.
            await holderTx.RollbackAsync(CancellationToken.None);
            responses = await Task.WhenAll(requests).WaitAsync(drainLimit, ct);
        }

        try
        {
            if (waiting != raceSize)
            {
                Assert.Fail(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{raceSize} registrations queued at their insert were not observed within "
                    + $"{observationLimit.TotalSeconds} s (last count: {waiting})."));
            }

            Assert.Single(responses, static response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(
                raceSize - 1,
                responses.Count(static response => response.StatusCode == HttpStatusCode.Conflict));
            foreach (HttpResponseMessage conflict in responses.Where(
                static response => response.StatusCode == HttpStatusCode.Conflict))
            {
                await ProblemAssert.AssertProblemAsync(conflict, HttpStatusCode.Conflict, cancellationToken: ct);
            }

            int stored = await variant.ExecuteDbAsync(
                (db, token) => db.Guests.AsNoTracking().CountAsync(guest => guest.Email == email, token),
                ct);
            Assert.Equal(1, stored);

            // The winner's record shows that the provider receives the application's records; the losers add none
            // at Error.
            IReadOnlyList<SmartHotelApiFactory.CapturedLog> records = capture.Since(mark);
            Assert.Single(
                records,
                record => record.Category == authServiceCategory && record.EventId == guestRegisteredEventId);
            SmartHotelApiFactory.LogCapture.AssertNone(
                records,
                static record => record.Level >= LogLevel.Error,
                "Error or Critical record");
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }

        // Polls until expected sessions of the database, other than the holder and the observer itself, wait on a
        // lock while inserting a guest, or the limit passes, and returns the last count. No transaction is opened:
        // each poll is its own autocommit statement and so reads a fresh pg_stat_activity snapshot.
        static async Task<long> WaitForInsertWaitersAsync(
            string connectionString,
            int holderPid,
            int expected,
            TimeSpan limit,
            CancellationToken cancellationToken)
        {
            const string countInsertWaitersSql =
                "SELECT count(*) FROM pg_stat_activity "
                + "WHERE datname = current_database() AND pid <> @holderPid AND pid <> pg_backend_pid() "
                + "AND wait_event_type = 'Lock' AND query LIKE 'INSERT INTO \"Guests\"%'";
            TimeSpan pollInterval = TimeSpan.FromMilliseconds(50);

            await using NpgsqlConnection observer = new(connectionString);
            await observer.OpenAsync(cancellationToken);
            await using NpgsqlCommand countWaiters = new(countInsertWaitersSql, observer);
            countWaiters.Parameters.AddWithValue("holderPid", holderPid);

            Stopwatch elapsed = Stopwatch.StartNew();
            long count;
            do
            {
                count = (long)(await countWaiters.ExecuteScalarAsync(cancellationToken))!;
                if (count == expected)
                {
                    break;
                }

                await Task.Delay(pollInterval, cancellationToken);
            }
            while (elapsed.Elapsed < limit);

            return count;
        }
    }

    [Fact]
    public async Task Register_Valid_Returns201WithoutHash()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        string rawEmail = $"  Mixed.Case+{Guid.NewGuid():N}@Example.TEST  ";
        string expectedEmail = rawEmail.Trim().ToLowerInvariant();

        using HttpResponseMessage response = await client.RegisterAsync(
            new { email = rawEmail, password = ValidPassword, fullName = ValidFullName },
            ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // A boolean with a fixed message rather than Assert.Null, which would print a regressed Location URI, and a
        // URI that identifies a guest could carry the email.
        Assert.True(
            response.Headers.Location is null,
            "Expected the registration response to have no Location header.");

        int guestId = await ReadGuestIdAsync(response, ct);

        Guest guest = await factory.ExecuteDbAsync(
            (db, token) => db.Guests.AsNoTracking().SingleAsync(g => g.Id == guestId, token),
            ct);

        // The stored strings are checked with fixed messages rather than Assert.Equal/NotEqual, so a failure never
        // prints the email, the full name, the password or the hash. Ordinal equality is as exact as Assert.Equal.
        Assert.True(
            string.Equals(expectedEmail, guest.Email, StringComparison.Ordinal),
            "Expected the stored email to be the submitted email trimmed and in lower case.");
        Assert.True(
            string.Equals(ValidFullName, guest.FullName, StringComparison.Ordinal),
            "Expected the stored full name to equal the submitted full name.");
        Assert.Equal(DateTimeKind.Utc, guest.CreatedAt.Kind);

        Assert.False(string.IsNullOrEmpty(guest.PasswordHash), "Expected a stored password hash.");
        Assert.False(
            string.Equals(guest.PasswordHash, ValidPassword, StringComparison.Ordinal),
            "Expected the stored password hash to differ from the password.");

        byte[] hash = Convert.FromBase64String(guest.PasswordHash);
        Assert.Equal(V3HashLength, hash.Length);
        Assert.Equal(V3FormatMarker, hash[0]);
        Assert.Equal(HmacSha512Prf, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(1)));
        Assert.Equal(ConfiguredIterationCount, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5)));
        Assert.Equal(SaltLengthBytes, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(9)));
    }

    // 8 such characters are 16 UTF-16 code units and 128 are 256, so both bounds hold only when they count characters.
    [Theory]
    [InlineData(8)]
    [InlineData(128)]
    public async Task Register_SupplementaryPasswordAtLengthBound_Returns201AndLogsIn(int characterCount)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        string email = UniqueEmail("supplementary");
        string password = SupplementaryPassword(characterCount);

        using HttpResponseMessage registered = await client.RegisterAsync(
            new { email, password, fullName = ValidFullName },
            ct);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        // The 200 shows the stored hash is of the password exactly as sent. Only status codes reach a failure
        // message, never the password.
        using HttpResponseMessage loggedIn = await client.LoginAsync(new { email, password }, ct);
        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
    }

    private static string UniqueEmail(string prefix)
    {
        // Every test in the class shares one database and runs in any order, so each address must be new.
        return $"{prefix}+{Guid.NewGuid():N}@example.test";
    }

    private static string SupplementaryPassword(int characterCount)
    {
        return string.Concat(Enumerable.Repeat(SupplementaryCharacter, characterCount));
    }

    // Each case breaks exactly one input and keeps the others valid: MVC runs RegisterRequest.Validate, which reports
    // the email errors, only after every attribute check has passed, so a second broken field would hide the key.
    private static Task<HttpResponseMessage> SendInvalidRegistrationAsync(
        ApiTestClient client,
        string caseLabel,
        CancellationToken cancellationToken)
    {
        return caseLabel switch
        {
            "email-not-an-address" => client.RegisterAsync(
                new { email = "not-an-email", password = ValidPassword, fullName = ValidFullName },
                cancellationToken),
            "email-non-ascii" => client.RegisterAsync(
                new
                {
                    email = $"g\u00e4st+{Guid.NewGuid():N}@example.test",
                    password = ValidPassword,
                    fullName = ValidFullName,
                },
                cancellationToken),
            "password-too-short" => client.RegisterAsync(
                new { email = UniqueEmail("short"), password = "Passw0r", fullName = ValidFullName },
                cancellationToken),
            // 4 characters in 8 UTF-16 code units, and 129 characters in 258.
            "password-supplementary-too-short" => client.RegisterAsync(
                new { email = UniqueEmail("short"), password = SupplementaryPassword(4), fullName = ValidFullName },
                cancellationToken),
            "password-supplementary-too-long" => client.RegisterAsync(
                new { email = UniqueEmail("long"), password = SupplementaryPassword(129), fullName = ValidFullName },
                cancellationToken),
            "fullname-missing" => client.RegisterAsync(
                new { email = UniqueEmail("noname"), password = ValidPassword },
                cancellationToken),
            "empty-json-body" => PostRawJsonAsync(client, string.Empty, cancellationToken),
            "malformed-json" => PostRawJsonAsync(client, "{\"email\":", cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(caseLabel), caseLabel, "Unknown registration case."),
        };
    }

    private static async Task<HttpResponseMessage> PostRawJsonAsync(
        ApiTestClient client,
        string json,
        CancellationToken cancellationToken)
    {
        using StringContent content = new(json, Encoding.UTF8, JsonMediaType);
        return await client.Http.PostAsync(RegisterRoute, content, cancellationToken);
    }

    // Reads the 201 body as raw JSON rather than a typed record, so any member beyond guestId, such as a password
    // hash, fails the test. A regression could name a member after the guest's email or put it in a value, so no
    // member name, value or parser message from the response reaches a failure message; at most the member count does.
    private static async Task<int> ReadGuestIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        JsonElement root;
        await using (Stream body = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // The parser's message quotes characters of the offending input.
                throw FailException.ForFailure(
                    "Expected the registration response to be a JSON document but it could not be parsed.");
            }
        }

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        string[] memberNames = [.. root.EnumerateObject().Select(static member => member.Name)];
        Assert.True(
            memberNames.Length == 1 && string.Equals("guestId", memberNames[0], StringComparison.Ordinal),
            memberNames.Length == 1
                ? "Expected the registration response to have exactly one member, guestId, but its one member has "
                    + "a different name."
                : "Expected the registration response to have exactly one member, guestId, but it has "
                    + $"{memberNames.Length} members.");

        JsonElement guestIdValue = root.GetProperty("guestId");
        Assert.Equal(JsonValueKind.Number, guestIdValue.ValueKind);
        Assert.True(guestIdValue.TryGetInt32(out int guestId), "Expected guestId to be a 32-bit integer.");
        Assert.True(guestId > 0, "Expected guestId to be a positive integer.");

        return guestId;
    }

    [Theory]
    [InlineData("acknowledgement-lost")]
    [InlineData("commit-after-check")]
    public async Task Register_CommitAcknowledgementLost_ReturnsCreatedWithoutSecondGuest(string caseLabel)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InsertLoss loss = caseLabel switch
        {
            "acknowledgement-lost" => InsertLoss.AcknowledgementLost,
            "commit-after-check" => InsertLoss.CommitAfterCheck,
            _ => throw new ArgumentOutOfRangeException(nameof(caseLabel), caseLabel, "Unknown acknowledgement case."),
        };
        string email = UniqueEmail("lost-ack");
        LoseGuestInsertAcknowledgement interceptor = new(loss, email, UnpooledConnectionString());

        RegisterResult result = await RegisterLosingFirstInsertAsync(email, interceptor, ct);

        Assert.True(interceptor.FailureReported, "No transient failure was reported for the guest insert.");
        Assert.Equal(loss == InsertLoss.CommitAfterCheck, interceptor.LateInsertCommitted);
        RegisterResult.Created created = Assert.IsType<RegisterResult.Created>(result);

        // The committed insert was recognized as this request's own: the result names its row, and no second guest
        // was stored.
        List<int> guestIds = await factory.ExecuteDbAsync(
            (db, token) => db.Guests
                .AsNoTracking()
                .Where(g => g.Email == email)
                .Select(static g => g.Id)
                .ToListAsync(token),
            ct);
        Assert.Equal(created.GuestId, Assert.Single(guestIds));
    }

    [Fact]
    public async Task Register_CompetitorTakesEmailWhileLostInsertRetries_ReturnsEmailTaken()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = UniqueEmail("lost-ack-race");
        LoseGuestInsertAcknowledgement interceptor = new(
            InsertLoss.CompetitorAfterCheck,
            email,
            UnpooledConnectionString());

        RegisterResult result = await RegisterLosingFirstInsertAsync(email, interceptor, ct);

        Assert.True(interceptor.FailureReported, "No transient failure was reported for the guest insert.");
        Assert.True(interceptor.LateInsertCommitted, "The competing registration was not stored.");

        // The retry's insert met a row without this request's hash, so another registration won the race: a transient
        // failure on the way does not turn that 409 into a 201.
        Assert.IsType<RegisterResult.EmailTaken>(result);

        List<string> fullNames = await factory.ExecuteDbAsync(
            (db, token) => db.Guests
                .AsNoTracking()
                .Where(g => g.Email == email)
                .Select(static g => g.FullName)
                .ToListAsync(token),
            ct);
        Assert.Equal(LoseGuestInsertAcknowledgement.CompetitorFullName, Assert.Single(fullNames));
    }

    // No request can lose an acknowledgement on purpose, so the service runs here on a context of its own, under the
    // API's retrying strategy, with an interceptor that reports one transient failure for the first guest insert.
    private async Task<RegisterResult> RegisterLosingFirstInsertAsync(
        string email,
        LoseGuestInsertAcknowledgement interceptor,
        CancellationToken cancellationToken)
    {
        DbContextOptions<SmartHotelDbContext> options = new DbContextOptionsBuilder<SmartHotelDbContext>()
            .UseNpgsql(
                UnpooledConnectionString(),
                npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), errorCodesToAdd: null))
            .AddInterceptors(interceptor)
            .Options;

        await using SmartHotelDbContext db = new(options);
        AuthService service = new(
            db,
            factory.Services.GetRequiredService<IPasswordHasher<Guest>>(),
            factory.Services.GetRequiredService<TokenService>(),
            TimeProvider.System,
            NullLogger<AuthService>.Instance);
        return await service.RegisterAsync(new RegisterRequest(email, ValidPassword, ValidFullName), cancellationToken);
    }

    // The test's own sessions bypass the pool the API draws from.
    private string UnpooledConnectionString()
    {
        return new NpgsqlConnectionStringBuilder(factory.DatabaseConnectionString) { Pooling = false }
            .ConnectionString;
    }

    // How the first attempt's guest insert is lost.
    private enum InsertLoss
    {
        // The insert commits, and the failure is reported before its acknowledgement arrives.
        AcknowledgementLost,

        // The insert is held back when the failure is reported, and a copy of it commits after the check that follows
        // the failure, as an insert that the server completes late would.
        CommitAfterCheck,

        // The insert is held back when the failure is reported and never commits. Another registration of the same
        // email commits after the check that follows the failure.
        CompetitorAfterCheck,
    }

    // Reports one transient failure for the first guest insert, as a connection lost after the server received the
    // insert would. A late insert, when the case has one, commits on a session of its own just before the next guest
    // insert is sent.
    private sealed class LoseGuestInsertAcknowledgement(InsertLoss loss, string email, string connectionString)
        : DbCommandInterceptor
    {
        public const string CompetitorFullName = "Competing Guest";

        // The competitor's hash is never verified, so any text stands in for one.
        private const string CompetingInsertSql =
            "INSERT INTO \"Guests\" (\"Email\", \"PasswordHash\", \"FullName\", \"CreatedAt\") "
            + "VALUES (@email, 'competing-registration', @fullName, now())";

        private int _failed;
        private int _lateCommitted;
        private NpgsqlCommand? _lateInsert;

        public bool FailureReported => Volatile.Read(ref _failed) == 1;

        public bool LateInsertCommitted => Volatile.Read(ref _lateCommitted) == 1;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (loss == InsertLoss.AcknowledgementLost || !IsGuestInsert(command))
            {
                return result;
            }

            if (Interlocked.Exchange(ref _failed, 1) == 0)
            {
                // Nothing is sent. What commits later is a copy of this insert or the competitor's.
                _lateInsert = loss == InsertLoss.CommitAfterCheck
                    ? ((NpgsqlCommand)command).Clone()
                    : CompetingInsert();
                throw ConnectionLost();
            }

            if (_lateInsert is NpgsqlCommand lateInsert)
            {
                _lateInsert = null;
                await using (lateInsert)
                {
                    await using NpgsqlConnection connection = new(connectionString);
                    await connection.OpenAsync(cancellationToken);
                    lateInsert.Connection = connection;
                    await lateInsert.ExecuteNonQueryAsync(cancellationToken);
                }

                Volatile.Write(ref _lateCommitted, 1);
            }

            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (loss != InsertLoss.AcknowledgementLost
                || !IsGuestInsert(command)
                || Interlocked.Exchange(ref _failed, 1) == 1)
            {
                return result;
            }

            // Closing the reader reads the server's replies to the end of the statement, so the insert's implicit
            // transaction has committed before the failure is reported.
            await result.CloseAsync();
            throw ConnectionLost();
        }

        private static bool IsGuestInsert(DbCommand command)
        {
            return command.CommandText.Contains("INSERT INTO \"Guests\"", StringComparison.Ordinal);
        }

        private static NpgsqlException ConnectionLost()
        {
            return new NpgsqlException(
                "The connection was lost before the insert was acknowledged.",
                new IOException("The connection was reset."));
        }

        private NpgsqlCommand CompetingInsert()
        {
            NpgsqlCommand insert = new(CompetingInsertSql);
            insert.Parameters.AddWithValue("email", email);
            insert.Parameters.AddWithValue("fullName", CompetitorFullName);
            return insert;
        }
    }
}