using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 2026-11-15 ADAPTOR-TY-HB-4: take_rate_token read its "now" before it locked the bucket row. A caller that waited
// for the lock wrote that older time over the lock holder's newer one, and the next caller counted the time in between
// twice. Two real sessions: one holds the row while the other waits in the function. The function the migration
// 20261115205317_RateTokenTimeAfterLock writes never moves the bucket's time back and hands out no extra token; the body
// it replaces (its Down, the one live runs) does both, run here from a schema of its own.
[Collection("postgres-integration")]
public sealed class SharedRateLimiterClockPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string OldSchema = "rate_token_before";
    // One token at most, refilled every 2 s; the holder keeps the row for 2.5 s, worth 1.25 tokens.
    private const double Capacity = 1;
    private const double TokensPerSecond = 0.5;
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(2.5);

    [PostgreSqlFact]
    public async Task A_caller_waiting_for_the_bucket_never_moves_its_time_back_nor_takes_an_extra_token()
    {
        var outcome = await RunAsync("integration", $"clock-after-lock-{Guid.NewGuid():N}");
        Assert.True(outcome.TimeAfterWaiter >= outcome.TimeAfterHolder, $"{outcome.TimeAfterWaiter:O} before {outcome.TimeAfterHolder:O}");
        Assert.True(outcome.Granted <= outcome.Allowed, $"{outcome.Granted} tokens, {outcome.Allowed} allowed");
        Assert.Equal(2, outcome.Granted);
    }

    // The body live runs until the migration: the waiter writes its older time and the next caller takes a third token
    // where two were allowed. This is what the migration fixes; the test keeps the proof that the scenario reaches it.
    [PostgreSqlFact]
    public async Task The_replaced_body_moved_the_time_back_and_handed_out_an_extra_token()
    {
        await using var db = fixture.CreateContext();
        var down = OldBody();
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA IF NOT EXISTS {OldSchema}");
        try
        {
            await db.Database.ExecuteSqlRawAsync(down.Replace("integration.take_rate_token(", $"{OldSchema}.take_rate_token(", StringComparison.Ordinal));
            // The body Down restores is the one live runs (md5 of pg_proc.prosrc on live, 15 Nov 2026).
            var md5 = await db.Database.SqlQueryRaw<string>($"SELECT md5(p.prosrc) AS \"Value\" FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = '{OldSchema}' AND p.proname = 'take_rate_token'").SingleAsync();
            Assert.Equal("ff930b826eca10c217f7d72d041e7cc8", md5);

            var outcome = await RunAsync(OldSchema, $"clock-before-lock-{Guid.NewGuid():N}");
            Assert.True(outcome.TimeAfterWaiter < outcome.TimeAfterHolder);
            Assert.True(outcome.Granted > outcome.Allowed, $"{outcome.Granted} tokens, {outcome.Allowed} allowed");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA IF EXISTS {OldSchema} CASCADE");
        }
    }

    // The migration only replaces the function: same arguments and result, and the database still holds one of it.
    [PostgreSqlFact]
    public async Task The_migration_keeps_the_function_signature()
    {
        await using var db = fixture.CreateContext();
        var signatures = await db.Database.SqlQueryRaw<string>("SELECT pg_get_function_identity_arguments(p.oid) || ' -> ' || pg_get_function_result(p.oid) AS \"Value\" FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'integration' AND p.proname = 'take_rate_token'").ToListAsync();
        Assert.Equal(["p_key character varying, p_capacity double precision, p_tokens_per_second double precision -> double precision"], signatures);
    }

    private static string OldBody()
    {
        var operations = new RateTokenTimeAfterLock().DownOperations;
        return Assert.IsType<SqlOperation>(Assert.Single(operations)).Sql;
    }

    private sealed record Outcome(int Granted, int Allowed, DateTimeOffset TimeAfterHolder, DateTimeOffset TimeAfterWaiter);

    private async Task<Outcome> RunAsync(string schema, string key)
    {
        await using var holderDb = fixture.CreateContext();
        await using var waiterDb = fixture.CreateContext();
        var holder = (NpgsqlConnection)holderDb.Database.GetDbConnection();
        var waiter = (NpgsqlConnection)waiterDb.Database.GetDbConnection();
        await holder.OpenAsync();
        await waiter.OpenAsync();
        var granted = 0;

        // The bucket starts empty: its one token is taken at once.
        var start = await NowAsync(holder);
        if (await TakeAsync(holder, schema, key) == 0) granted++;

        // The holder locks the row; the waiter calls the function and waits for the lock.
        await using (var transaction = await holder.BeginTransactionAsync())
        {
            await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM integration.rate_limit_buckets WHERE \"Key\" = $1 FOR UPDATE", holder, transaction))
            {
                lockRow.Parameters.AddWithValue(key);
                await lockRow.ExecuteScalarAsync();
            }
            var waiting = TakeAsync(waiter, schema, key);
            await WaitUntilBlockedAsync(holder, transaction, waiter.ProcessID);
            await Task.Delay(Hold);
            // The holder takes the token the hold refilled and writes its time, then lets the waiter in.
            await using (var take = new NpgsqlCommand($"SELECT {schema}.take_rate_token($1, $2, $3)", holder, transaction))
            {
                take.Parameters.AddWithValue(key);
                take.Parameters.AddWithValue(Capacity);
                take.Parameters.AddWithValue(TokensPerSecond);
                if ((double)(await take.ExecuteScalarAsync())! == 0) granted++;
            }
            var timeAfterHolder = await UpdatedAtAsync(holder, key, transaction);
            await transaction.CommitAsync();
            if (await waiting == 0) granted++;
            var timeAfterWaiter = await UpdatedAtAsync(holder, key);

            // The next caller, right after.
            if (await TakeAsync(holder, schema, key) == 0) granted++;
            var end = await NowAsync(holder);
            var allowed = (int)Math.Floor(Capacity + (end - start).TotalSeconds * TokensPerSecond);
            return new Outcome(granted, allowed, timeAfterHolder, timeAfterWaiter);
        }
    }

    private static async Task<double> TakeAsync(NpgsqlConnection connection, string schema, string key)
    {
        await using var command = new NpgsqlCommand($"SELECT {schema}.take_rate_token($1, $2, $3)", connection);
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(Capacity);
        command.Parameters.AddWithValue(TokensPerSecond);
        return (double)(await command.ExecuteScalarAsync())!;
    }

    // The waiter's session shows as waiting for a lock before the hold starts.
    private static async Task WaitUntilBlockedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int waiterProcessId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE pid = $1 AND wait_event_type = 'Lock'", connection, transaction);
            command.Parameters.AddWithValue(waiterProcessId);
            if ((long)(await command.ExecuteScalarAsync())! == 1) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The waiter never waited for the bucket's lock.");
    }

    private static async Task<DateTimeOffset> UpdatedAtAsync(NpgsqlConnection connection, string key, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("SELECT \"UpdatedAt\" FROM integration.rate_limit_buckets WHERE \"Key\" = $1", connection, transaction);
        command.Parameters.AddWithValue(key);
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync())!, TimeSpan.Zero);
    }

    private static async Task<DateTimeOffset> NowAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync())!, TimeSpan.Zero);
    }
}
