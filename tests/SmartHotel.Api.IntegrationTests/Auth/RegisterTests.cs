using System.Buffers.Binary;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

    // The fixed field messages of the registration contract, one per broken bound. None names its field: the
    // errors key does.
    private const string EmailMessage =
        "The value must be a valid email address of at most 254 characters, using only ASCII characters.";
    private const string PasswordLengthMessage = "The value must be 8 to 128 characters long.";
    private const string PasswordNulMessage = "The value contains a character that is not allowed.";
    private const string FullNameRequiredMessage = "A value is required.";
    private const string FullNameLengthMessage = "The value must not be blank and must be at most 200 characters.";
    private const string FullNameNulMessage = "The value contains a character that is not allowed.";

    // RFC 5321's limits, which registration applies: an address of at most 254 characters, a local part of at most 64,
    // and domain labels of at most 63 (RFC 1035).
    private const int MaxEmailLength = 254;
    private const int MaxLocalPartLength = 64;
    private const int MaxDomainLabelLength = 63;

    // The registration limit on the trimmed full name, in characters (Unicode scalar values).
    private const int MaxFullNameLength = 200;

    // U+1F600, a character outside the Basic Multilingual Plane: one Unicode scalar value but two UTF-16 code units,
    // so a password or full name built from it meets the registration bounds only when they count characters.
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
    [InlineData("email-too-long", "email")]
    [InlineData("email-local-part-too-long", "email")]
    [InlineData("password-too-short", "password")]
    [InlineData("password-supplementary-too-short", "password")]
    [InlineData("password-supplementary-too-long", "password")]
    [InlineData("password-nul-inside", "password")]
    [InlineData("password-nul-appended", "password")]
    [InlineData("fullname-missing", "fullName")]
    [InlineData("fullname-blank", "fullName")]
    [InlineData("fullname-too-long", "fullName")]
    [InlineData("fullname-supplementary-too-long", "fullName")]
    [InlineData("fullname-nul", "fullName")]
    [InlineData("empty-json-body", "$")]
    [InlineData("malformed-json", null)]
    public async Task Register_InvalidInput_Returns400(string caseLabel, string? expectedErrorKey)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();

        using HttpResponseMessage response = await SendInvalidRegistrationAsync(client, caseLabel, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedErrorKey, ct);
    }

    // Every field check is an attribute, like Required, so the password bound is reported in the same 400 as a missing
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

    // One submission that breaks several fields reports every one of them, each with exactly the one fixed message of
    // the bound it breaks, so the guest can correct them all in one round trip.
    [Theory]
    [InlineData("email-and-short-password")]
    [InlineData("email-and-blank-fullname")]
    [InlineData("email-and-long-fullname")]
    [InlineData("every-field")]
    public async Task Register_SeveralInvalidFields_Returns400WithEveryKey(string caseLabel)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        (object body, (string Key, string Message)[] expected) = caseLabel switch
        {
            "email-and-short-password" => (
                (object)new { email = "not-an-email", password = "short77", fullName = ValidFullName },
                new[] { ("email", EmailMessage), ("password", PasswordLengthMessage) }),
            "email-and-blank-fullname" => (
                new { email = "not-an-email", password = ValidPassword, fullName = " \t " },
                new[] { ("email", EmailMessage), ("fullName", FullNameRequiredMessage) }),
            "email-and-long-fullname" => (
                new
                {
                    email = "not-an-email",
                    password = ValidPassword,
                    fullName = new string('n', MaxFullNameLength + 1),
                },
                new[] { ("email", EmailMessage), ("fullName", FullNameLengthMessage) }),
            "every-field" => (
                new { email = "not-an-email", password = $"{ValidPassword}\u0000", fullName = "Test\u0000Guest" },
                new[] { ("email", EmailMessage), ("password", PasswordNulMessage), ("fullName", FullNameNulMessage) }),
            _ => throw new ArgumentOutOfRangeException(nameof(caseLabel), caseLabel, "Unknown registration case."),
        };

        using HttpResponseMessage response = await client.RegisterAsync(body, ct);

        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            expected[0].Key,
            ct);

        // ProblemAssert has checked that errors is an object whose values are all arrays of message strings and that it
        // holds the first expected key once. Comparing every member name, repeats included, with the expected keys then
        // shows that errors holds exactly the broken fields' keys, each once. The keys and messages are compared in
        // memory, and a failure names only the test's own fixed key.
        JsonElement errors = problem.GetProperty("errors");
        string[] keys = [.. errors.EnumerateObject().Select(static entry => entry.Name).Order(StringComparer.Ordinal)];
        string[] expectedKeys = [.. expected.Select(static entry => entry.Key).Order(StringComparer.Ordinal)];
        Assert.True(
            keys.SequenceEqual(expectedKeys, StringComparer.Ordinal),
            $"Expected errors to hold exactly the {expectedKeys.Length} broken fields' keys but it holds {keys.Length} "
                + "key(s) that differ from them.");

        foreach ((string key, string message) in expected)
        {
            JsonElement messages = errors.GetProperty(key);
            Assert.True(
                messages.GetArrayLength() == 1
                    && string.Equals(message, messages[0].GetString(), StringComparison.Ordinal),
                $"Expected errors.{key} to hold exactly the one fixed message of the bound the case breaks.");
        }
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

        // Bounds each cleanup step that is otherwise immediate: the release, the stop of the requests cancelled at the
        // drain limit, the host's finishing of the requests whose calls have ended, and the holder's disposal.
        TimeSpan cleanupStepLimit = TimeSpan.FromSeconds(10);
        string authServiceCategory = typeof(AuthService).FullName!;

        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.LogCapture capture = new();
        ServerRequestTracker serverRequests = new();
        SmartHotelApiFactory variant = factory.CreateLogCaptureVariant(
            capture,
            SmartHotelApiFactory.LogCapture.DataLayerRules,
            additionalOverrides: services => services.AddSingleton<IStartupFilter>(serverRequests));

        // Every failure is kept in the order it occurs, the test's own and the cleanup's alike, and all of them are
        // reported together at the end, so a failing cleanup step never hides an earlier failure.
        List<Exception> failures = [];
        List<Task<HttpResponseMessage>> requests = new(raceSize);
        List<HttpResponseMessage> responses = new(raceSize);
        try
        {
            // A hand-built factory is not a fixture, so it is started here.
            await variant.InitializeAsync();
            ApiTestClient client = variant.CreateApiClient();
            string email = UniqueEmail("race");
            int mark;

            // Disposed by the finally below rather than by await using, whose disposal can block after a failed
            // release, so that the wait for it is bounded like every other cleanup step.
            NpgsqlConnection holder = new(variant.DatabaseConnectionString);
            NpgsqlTransaction? holderTx = null;
            try
            {
                await holder.OpenAsync(ct);
                holderTx = await holder.BeginTransactionAsync(ct);
                await using (NpgsqlCommand lockGuests = new(lockGuestsSql, holder, holderTx))
                {
                    await lockGuests.ExecuteNonQueryAsync(ct);
                }

                mark = capture.Count;

                // The requests' own token: the test's cancellation reaches them, and so does the drain's cancellation
                // of the requests still running at its limit, which must not cancel the test's other work.
                using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    // Started without awaiting, so every request is in flight at once, each with the same address, and
                    // inside the try, so the ones already started are drained even when starting another fails.
                    for (int started = 0; started < raceSize; started++)
                    {
                        requests.Add(client.RegisterAsync(
                            new { email, password = ValidPassword, fullName = ValidFullName },
                            requestCancellation.Token));
                    }

                    (bool observed, long lastCount) = await WaitForInsertWaitersAsync(
                        variant,
                        holder.ProcessID,
                        raceSize,
                        observationLimit,
                        ct);
                    if (!observed)
                    {
                        failures.Add(FailException.ForFailure(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{raceSize} registrations queued at their insert were not observed within "
                            + $"{observationLimit.TotalSeconds} s (last count: {lastCount}).")));
                    }
                }
                catch (Exception exception)
                {
                    // Kept, and reported only once the lock is released and the requests are drained.
                    failures.Add(exception);
                }
                finally
                {
                    // Runs on success, on the observation timeout and on any failure, the test's cancellation included.
                    // No step throws, so each one runs whatever the one before it did. While the test runs, each step
                    // is bounded by its limit and by the test's token. Once that token is cancelled, before a step or
                    // during it, the step goes on bounded by its limit alone, so a cancelled test still releases the
                    // lock, drains the requests and waits for the host to finish them; a step the cancellation
                    // interrupts records it with the other failures, so the test still reports the cancellation.
                    await ReleaseLockAsync(holder, holderTx, cleanupStepLimit, failures, ct);
                    await DrainRequestsAsync(
                        requests,
                        requestCancellation,
                        drainLimit,
                        cleanupStepLimit,
                        responses,
                        failures,
                        ct);
                    await WaitForServerRequestsAsync(serverRequests, cleanupStepLimit, failures, ct);
                }
            }
            finally
            {
                // After the host's wait, so a disposal that blocks holds up neither the drain nor that wait. Bounded
                // like the steps above.
                await DisposeHolderAsync(holder, holderTx, cleanupStepLimit, failures, ct);
            }

            // The outcome is asserted only after a complete, observed race.
            if (failures.Count == 0)
            {
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

                // The winner's record shows that the provider receives the application's records; the losers add
                // none at Error. The host has finished every request, so each request's records are all in.
                IReadOnlyList<SmartHotelApiFactory.CapturedLog> records = capture.Since(mark);
                Assert.Single(
                    records,
                    record => record.Category == authServiceCategory && record.EventId == guestRegisteredEventId);
                SmartHotelApiFactory.LogCapture.AssertNone(
                    records,
                    static record => record.Level >= LogLevel.Error,
                    "Error or Critical record");
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }

            // DisposeAsync stops the host and drops its database whatever the test's token says, so it runs only once
            // no request is running in the host: every call has ended, after which no request can reach the host, and
            // the host has finished each request it received. Otherwise the disposal is handed over to run once that
            // holds, and the test reports why.
            Task requestsEnded = Task.WhenAll(requests.Select(EndedAsync));
            if (requestsEnded.IsCompleted && serverRequests.WhenIdleAsync().IsCompleted)
            {
                try
                {
                    await variant.DisposeAsync();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            else
            {
                failures.Add(FailException.ForFailure(
                    "Registration requests were still running when the test ended, so the API host and its database "
                    + "are disposed only once every one of them has ended."));
                _ = DisposeWhenIdleAsync(variant, requestsEnded, serverRequests, TestContext.Current);
            }
        }

        ThrowFailures(failures);

        // Polls until expected sessions of the database, other than the holder and the observer itself, wait on a
        // lock while inserting a guest. One deadline bounds opening the observer, every poll and every pause, and a
        // count read after it is not accepted. Returns whether the waiters were seen in time, with the last count
        // read in time (-1 for none). No transaction is opened: each poll is its own autocommit statement and so
        // reads a fresh pg_stat_activity snapshot.
        static async Task<(bool Observed, long LastCount)> WaitForInsertWaitersAsync(
            SmartHotelApiFactory host,
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

            Stopwatch elapsed = Stopwatch.StartNew();
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(limit);
            long lastCount = -1;
            try
            {
                await using NpgsqlConnection observer = await host.OpenDatabaseConnectionAsync(deadline.Token);
                await using NpgsqlCommand countWaiters = new(countInsertWaitersSql, observer);
                countWaiters.Parameters.AddWithValue("holderPid", holderPid);

                while (true)
                {
                    long count = (long)(await countWaiters.ExecuteScalarAsync(deadline.Token))!;
                    if (elapsed.Elapsed >= limit)
                    {
                        // Read after the deadline, so it cannot show the waiters in time.
                        return (false, lastCount);
                    }

                    lastCount = count;
                    if (count == expected)
                    {
                        return (true, count);
                    }

                    await Task.Delay(pollInterval, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                // The deadline passed while opening the observer, polling or pausing. The test's own cancellation is
                // not caught.
                return (false, lastCount);
            }
        }

        // Rolls back the holder's transaction, which changed nothing, so the lock is released. The rollback's token is
        // limited to limit and, while the test runs, linked to the test's; when the test has been cancelled before the
        // rollback starts, the limit alone bounds it. When the rollback fails, passes its limit or is interrupted by
        // the test's cancellation, closing the holder's connection still ends the transaction. CloseAsync takes no
        // token, so the wait for it is bounded instead, as EndsWithinAsync bounds a wait; a close still running then
        // is recorded as such and left to finish on its own. Failures, an interruption included, are recorded for the
        // caller, never thrown.
        static async Task ReleaseLockAsync(
            NpgsqlConnection holder,
            NpgsqlTransaction holderTx,
            TimeSpan limit,
            List<Exception> failures,
            CancellationToken cancellationToken)
        {
            // Once the test is cancelled, the rollback departs from the convention of passing the test's token to every
            // call that takes one, because that token would stop it before the lock is released.
            using CancellationTokenSource deadline = cancellationToken.IsCancellationRequested
                ? new CancellationTokenSource()
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(limit);
            try
            {
                await holderTx.RollbackAsync(deadline.Token);
                return;
            }
            catch (Exception rollbackFailure)
            {
                failures.Add(rollbackFailure);
            }

            await AwaitWithinAsync(
                CloseConnectionAsync(holder),
                limit,
                "The holder's connection had not closed",
                "after its rollback failed",
                failures,
                cancellationToken);
        }

        // Closes the connection; a failure, even one thrown before the close starts, faults the returned task.
        static async Task CloseConnectionAsync(NpgsqlConnection connection)
        {
            await connection.CloseAsync();
        }

        // Disposes the holder's transaction, if one was begun, then its connection. Either disposal can block after a
        // failed release, so the wait for them is bounded as EndsWithinAsync bounds a wait; one still running then is
        // recorded as such and left to finish on its own. Failures are recorded for the caller, never thrown.
        static async Task DisposeHolderAsync(
            NpgsqlConnection holder,
            NpgsqlTransaction? holderTx,
            TimeSpan limit,
            List<Exception> failures,
            CancellationToken cancellationToken)
        {
            await AwaitWithinAsync(
                DisposeConnectionAsync(holder, holderTx),
                limit,
                "The holder's connection had not been disposed",
                "after its disposal began",
                failures,
                cancellationToken);
        }

        // Disposes the transaction, if any, and then the connection, even when the transaction's disposal fails.
        static async Task DisposeConnectionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
        {
            try
            {
                if (transaction is not null)
                {
                    await transaction.DisposeAsync();
                }
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }

        // Waits for an operation that takes no token, as EndsWithinAsync bounds the wait, and records its failure, or a
        // fixed message naming what had not finished within the limit. One that has not finished is left to run;
        // EndedAsync observes its failure when it ends. Never throws.
        static async Task AwaitWithinAsync(
            Task operation,
            TimeSpan limit,
            string unfinished,
            string since,
            List<Exception> failures,
            CancellationToken cancellationToken)
        {
            if (!await EndsWithinAsync(EndedAsync(operation), limit, failures, cancellationToken))
            {
                failures.Add(FailException.ForFailure(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{unfinished} {limit.TotalSeconds} s {since}.")));
                return;
            }

            try
            {
                // Already completed, so this does not wait.
                await operation;
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        // Waits up to drainLimit for every request to end, as EndsWithinAsync bounds the wait: the test's cancellation
        // reaches the requests through their own token, and a cancelled test still waits for them to stop. The
        // requests still running at the limit are cancelled and waited for once more, up to stopLimit. Each request
        // is then harvested on its own: every response is collected for the caller to dispose, whatever happened to
        // the others, and each failed or cancelled request is recorded. One still running is recorded as not stopped
        // and disposes its response if it ends later. Failures are recorded for the caller, never thrown.
        static async Task DrainRequestsAsync(
            List<Task<HttpResponseMessage>> requests,
            CancellationTokenSource requestCancellation,
            TimeSpan drainLimit,
            TimeSpan stopLimit,
            List<HttpResponseMessage> responses,
            List<Exception> failures,
            CancellationToken cancellationToken)
        {
            // Completes once every request has ended, however each one ended, and never faults.
            Task allEnded = Task.WhenAll(requests.Select(EndedAsync));
            if (!await EndsWithinAsync(allEnded, drainLimit, failures, cancellationToken))
            {
                try
                {
                    requestCancellation.Cancel();
                }
                catch (AggregateException cancellationFailure)
                {
                    // Thrown once every cancellation callback has run, so every request has been told to stop.
                    failures.Add(cancellationFailure);
                }

                await EndsWithinAsync(allEnded, stopLimit, failures, cancellationToken);
            }

            List<Exception> requestFailures = [];
            int stillRunning = 0;
            foreach (Task<HttpResponseMessage> request in requests)
            {
                if (!request.IsCompleted)
                {
                    stillRunning++;
                    _ = DisposeWhenEndedAsync(request);
                    continue;
                }

                try
                {
                    // Already completed, so this does not wait.
                    responses.Add(await request);
                }
                catch (Exception exception)
                {
                    requestFailures.Add(exception);
                }
            }

            if (requestFailures.Count > 0)
            {
                failures.Add(new AggregateException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{requestFailures.Count} of {requests.Count} registration requests failed or were cancelled."),
                    requestFailures));
            }

            if (stillRunning > 0)
            {
                failures.Add(FailException.ForFailure(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{stillRunning} of {requests.Count} registration requests had not ended "
                    + $"{drainLimit.TotalSeconds} s after the release, nor {stopLimit.TotalSeconds} s after being "
                    + $"cancelled; each disposes its response if it ends later.")));
            }
        }

        // Completes when the request ends, however it ends, and never throws.
        static async Task EndedAsync(Task request)
        {
            await request.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // Whether the task, which never faults, completes within the limit. While the test runs, the wait also ends at
        // the test's cancellation, which is then recorded; from the cancellation on, the wait goes on bounded by the
        // limit alone, so the cleanup step it belongs to still finishes while the test is being cancelled.
        static async Task<bool> EndsWithinAsync(
            Task task,
            TimeSpan limit,
            List<Exception> failures,
            CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await task.WaitAsync(limit, cancellationToken);
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
                catch (OperationCanceledException interrupted) when (cancellationToken.IsCancellationRequested)
                {
                    failures.Add(interrupted);
                }
            }

            // Departs from the convention of passing the test's token to every call that takes one: the test is
            // cancelled by now, and that token would end the wait before the cleanup step finishes.
            try
            {
                await task.WaitAsync(limit, CancellationToken.None);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        // Takes over a request still running when the drain ended, and disposes its response if one arrives.
        static async Task DisposeWhenEndedAsync(Task<HttpResponseMessage> request)
        {
            await EndedAsync(request);
            if (request.IsCompletedSuccessfully)
            {
                (await request).Dispose();
            }
        }

        // Waits up to limit for the host to finish every request it received, as EndsWithinAsync bounds the wait. A
        // call ends once its response has started, before the host has run the rest of the pipeline and released the
        // request's services, so ended calls alone do not show that no request is still running in the host. Failures
        // are recorded for the caller, never thrown.
        static async Task WaitForServerRequestsAsync(
            ServerRequestTracker serverRequests,
            TimeSpan limit,
            List<Exception> failures,
            CancellationToken cancellationToken)
        {
            if (!await EndsWithinAsync(serverRequests.WhenIdleAsync(), limit, failures, cancellationToken))
            {
                failures.Add(FailException.ForFailure(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{serverRequests.Active} requests were still running in the API host "
                    + $"{limit.TotalSeconds} s after the drain.")));
            }
        }

        // Takes over disposing the variant when the test ends with requests still running. It waits for every call to
        // end, after which no request can reach the host, then for the host to finish each request it received, and
        // only then stops the host and drops its database, so neither goes away under a running request. A host whose
        // requests never end is therefore deliberately left undisposed, its database in place, rather than dropping
        // the database under them. The test has reported its outcome by then, so a failure here is caught and sent as
        // a diagnostic message naming only its type.
        static async Task DisposeWhenIdleAsync(
            SmartHotelApiFactory host,
            Task requestsEnded,
            ServerRequestTracker serverRequests,
            ITestContext testContext)
        {
            try
            {
                await requestsEnded;
                await serverRequests.WhenIdleAsync();
                await host.DisposeAsync();
            }
            catch (Exception exception)
            {
                testContext.SendDiagnosticMessage(
                    "Disposing the registration race's API host after its requests ended failed with {0}.",
                    exception.GetType().FullName);
            }
        }

        // Rethrows a single failure as it was first thrown, or reports several together in the order they occurred.
        static void ThrowFailures(List<Exception> failures)
        {
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Throw(failures[0]);
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(failures);
            }
        }
    }

    // Counts the requests the API host is running, and signals when it runs none. Registered as a startup filter, its
    // middleware runs ahead of everything Program adds, so a request is counted before its response can start. The
    // test server ends a call only once the response has started or the pipeline has thrown, a cancelled call
    // included, so every request the host received whose call has ended has been counted. A request is counted out
    // once the server has completed its response and run its completion callbacks, the disposal of the request's
    // services among them, or once the pipeline throws, when the test server runs no completion callback.
    private sealed class ServerRequestTracker : IStartupFilter
    {
        private readonly object _gate = new();
        private int _active;

        // Pending while the host runs a request, and completed when the last running one is counted out; null while
        // none runs.
        private TaskCompletionSource? _idle;

        public int Active
        {
            get
            {
                lock (_gate)
                {
                    return _active;
                }
            }
        }

        // Completes once the host runs no request, at once when it runs none now, and never faults. A request the
        // host receives later is not waited for, so once every call has ended, after which no request can reach the
        // host, it completes only when the host has finished every request.
        public Task WhenIdleAsync()
        {
            lock (_gate)
            {
                return _idle?.Task ?? Task.CompletedTask;
            }
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(TrackAsync);
                next(app);
            };
        }

        private async Task TrackAsync(HttpContext context, RequestDelegate next)
        {
            lock (_gate)
            {
                _active++;

                // Its waiters resume on the thread pool, never inside this lock or the request that ends last.
                _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            int ended = 0;
            try
            {
                // Registered before anything the pipeline adds, so it runs last: completion callbacks run in the
                // reverse order of their registration.
                context.Response.OnCompleted(() =>
                {
                    End(ref ended);
                    return Task.CompletedTask;
                });
                await next(context);
            }
            catch (Exception)
            {
                End(ref ended);
                throw;
            }
        }

        // Counts a request out once, however many of its ends run, and signals the waiters when it was the last one.
        private void End(ref int ended)
        {
            if (Interlocked.Exchange(ref ended, 1) == 0)
            {
                lock (_gate)
                {
                    if (--_active == 0)
                    {
                        _idle?.TrySetResult();
                        _idle = null;
                    }
                }
            }
        }
    }

    [Fact]
    public async Task Register_Valid_Returns201WithoutHash()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        string rawEmail = $"  Mixed.Case+{Guid.NewGuid():N}@Example.TEST  ";
        string expectedEmail = rawEmail.Trim().ToLowerInvariant();

        // Surrounding white space, so the stored value shows that the full name is trimmed.
        using HttpResponseMessage response = await client.RegisterAsync(
            new { email = rawEmail, password = ValidPassword, fullName = $" \t{ValidFullName}  " },
            ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Compared in memory with a fixed message, so the response's own media type never reaches the test output.
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.True(
            string.Equals(JsonMediaType, mediaType, StringComparison.Ordinal),
            mediaType is null
                ? $"Expected the registration response's media type to be {JsonMediaType} but it has no valid "
                    + "Content-Type."
                : $"Expected the registration response's media type to be {JsonMediaType} but it has a different one.");

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
            "Expected the stored full name to be the submitted full name trimmed.");
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
        string password = SupplementaryText(characterCount);

        using HttpResponseMessage registered = await client.RegisterAsync(
            new { email, password, fullName = ValidFullName },
            ct);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        // The 200 shows the stored hash is of the password exactly as sent. Only status codes reach a failure
        // message, never the password.
        using HttpResponseMessage loggedIn = await client.LoginAsync(new { email, password }, ct);
        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
    }

    // Each case puts one input exactly at its bound and keeps the others valid. The full-name limit counts characters
    // of the trimmed value, so 200 supplementary characters (400 UTF-16 code units) and 200 characters inside
    // surrounding white space are within it.
    [Theory]
    [InlineData("fullname-at-limit")]
    [InlineData("fullname-supplementary-at-limit")]
    [InlineData("fullname-padded-at-limit")]
    [InlineData("email-at-limit")]
    [InlineData("email-local-part-at-limit")]
    public async Task Register_InputAtBound_Returns201AndStoresTrimmedValues(string caseLabel)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        (string email, string fullName) = caseLabel switch
        {
            "fullname-at-limit" => (UniqueEmail("name"), new string('n', MaxFullNameLength)),
            "fullname-supplementary-at-limit" => (UniqueEmail("name"), SupplementaryText(MaxFullNameLength)),
            "fullname-padded-at-limit" => (UniqueEmail("name"), $" \t {new string('n', MaxFullNameLength)}\n "),
            "email-at-limit" => (EmailOfLength(MaxEmailLength), ValidFullName),
            "email-local-part-at-limit" => ($"{UniqueLocalPart(MaxLocalPartLength)}@example.test", ValidFullName),
            _ => throw new ArgumentOutOfRangeException(nameof(caseLabel), caseLabel, "Unknown registration case."),
        };

        using HttpResponseMessage response = await client.RegisterAsync(
            new { email, password = ValidPassword, fullName },
            ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        int guestId = await ReadGuestIdAsync(response, ct);

        Guest guest = await factory.ExecuteDbAsync(
            (db, token) => db.Guests.AsNoTracking().SingleAsync(g => g.Id == guestId, token),
            ct);

        // Fixed messages, so a failure never prints the email or the full name.
        Assert.True(
            string.Equals(email.Trim().ToLowerInvariant(), guest.Email, StringComparison.Ordinal),
            "Expected the stored email to be the submitted email trimmed and in lower case.");
        Assert.True(
            string.Equals(fullName.Trim(), guest.FullName, StringComparison.Ordinal),
            "Expected the stored full name to be the submitted full name trimmed.");
    }

    private static string UniqueEmail(string prefix)
    {
        // Every test in the class shares one database and runs in any order, so each address must be new.
        return $"{prefix}+{Guid.NewGuid():N}@example.test";
    }

    private static string SupplementaryText(int characterCount)
    {
        return string.Concat(Enumerable.Repeat(SupplementaryCharacter, characterCount));
    }

    // A valid local part of exactly length characters (32 or more) that ends in a fresh GUID, so the address it starts
    // is new to the class's shared database.
    private static string UniqueLocalPart(int length)
    {
        string unique = Guid.NewGuid().ToString("N");
        ArgumentOutOfRangeException.ThrowIfLessThan(length, unique.Length);

        return new string('l', length - unique.Length) + unique;
    }

    // A new address of exactly length characters that breaks no bound but, past 254, its length: a local part of the
    // most characters RFC 5321 allows, then a domain of two labels of the most characters a label may hold, a label of
    // the remaining length and "test". length is 199 to 261, so that the remaining label holds 1 to 63 characters.
    private static string EmailOfLength(int length)
    {
        string fullLabels = $"{new string('a', MaxDomainLabelLength)}.{new string('b', MaxDomainLabelLength)}.";
        const string topLevelLabel = ".test";
        int lastLabelLength = length - MaxLocalPartLength - "@".Length - fullLabels.Length - topLevelLabel.Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(lastLabelLength, 1, nameof(length));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lastLabelLength, MaxDomainLabelLength, nameof(length));

        return $"{UniqueLocalPart(MaxLocalPartLength)}@{fullLabels}{new string('c', lastLabelLength)}{topLevelLabel}";
    }

    // Each case breaks exactly one input and keeps the others valid, so its 400 can fail only for the stated reason.
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
            // 255 characters in all, with a 64-character local part and domain labels within their limits.
            "email-too-long" => client.RegisterAsync(
                new { email = EmailOfLength(MaxEmailLength + 1), password = ValidPassword, fullName = ValidFullName },
                cancellationToken),
            "email-local-part-too-long" => client.RegisterAsync(
                new
                {
                    email = $"{UniqueLocalPart(MaxLocalPartLength + 1)}@example.test",
                    password = ValidPassword,
                    fullName = ValidFullName,
                },
                cancellationToken),
            "password-too-short" => client.RegisterAsync(
                new { email = UniqueEmail("short"), password = "Passw0r", fullName = ValidFullName },
                cancellationToken),
            // 4 characters in 8 UTF-16 code units, and 129 characters in 258.
            "password-supplementary-too-short" => client.RegisterAsync(
                new { email = UniqueEmail("short"), password = SupplementaryText(4), fullName = ValidFullName },
                cancellationToken),
            "password-supplementary-too-long" => client.RegisterAsync(
                new { email = UniqueEmail("long"), password = SupplementaryText(129), fullName = ValidFullName },
                cancellationToken),
            // Within the length bounds. A password followed by U+0000 would otherwise hash like the password itself.
            "password-nul-inside" => client.RegisterAsync(
                new { email = UniqueEmail("nul"), password = "Passw0rd\u0000!x", fullName = ValidFullName },
                cancellationToken),
            "password-nul-appended" => client.RegisterAsync(
                new { email = UniqueEmail("nul"), password = $"{ValidPassword}\u0000", fullName = ValidFullName },
                cancellationToken),
            "fullname-missing" => client.RegisterAsync(
                new { email = UniqueEmail("noname"), password = ValidPassword },
                cancellationToken),
            "fullname-blank" => client.RegisterAsync(
                new { email = UniqueEmail("blank"), password = ValidPassword, fullName = " \t\n " },
                cancellationToken),
            // 201 characters once the surrounding white space is trimmed.
            "fullname-too-long" => client.RegisterAsync(
                new
                {
                    email = UniqueEmail("longname"),
                    password = ValidPassword,
                    fullName = $"  {new string('n', MaxFullNameLength + 1)}  ",
                },
                cancellationToken),
            // 201 characters in 402 UTF-16 code units.
            "fullname-supplementary-too-long" => client.RegisterAsync(
                new
                {
                    email = UniqueEmail("longname"),
                    password = ValidPassword,
                    fullName = SupplementaryText(MaxFullNameLength + 1),
                },
                cancellationToken),
            "fullname-nul" => client.RegisterAsync(
                new { email = UniqueEmail("nul"), password = ValidPassword, fullName = "Test\u0000Guest" },
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