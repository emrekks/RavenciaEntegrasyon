using System.Globalization;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 2026-11-15 FATURA-FINANS-2: the overview and the reports counted an accepted return by a word list of their own,
// the invoice by ReturnDecisionLinePolicy.StockDispositionAllowed. Owner (15 Nov 2026): one rule, the invoice's.
// AcceptedReturnPolicy.LineCountsInDatabase is that function as SQL; these tests hold the two to the same answer for
// every claim status and every status word, and pin which combinations the old report rule answered otherwise.
public sealed class AcceptedReturnRuleParityTests
{
    // Every word Canonicalize knows, its spellings (case, separators, spaces), words it does not know and no status.
    internal static readonly string?[] LineStatuses =
    [
        null, "", " ", "--", "Accepted", "accepted", "ACCEPTED", "Accepted ", "Approved", "Completed", "Refunded", "Open", "CLOSED",
        "Created", "NewRequest", "WaitingInAction", "Waiting_In_Action", "AwaitingAction", "AwaitingPreApproval", "InAnalysis",
        "WaitingFraudCheck", "WaitingForSellerAction", "Requested", "WaitingForShipment", "WaitingInCargo", "Waiting In Cargo",
        "InTransit", "ReturnInTransit", "Shipped", "Unresolved", "InDispute", "Disputed", "Rejected", "Declined", "Cancelled",
        "Canceled", "Manual_Refund", "SomethingNew", "1",
        // FATURA-FINANS-2: a letter or digit outside ASCII is kept by Canonicalize (unknown), anything else outside ASCII
        // is skipped like a separator; the long s reads as S, the other letters do not fold to ASCII.
        "ACCEPTEDé", "Acceptéd", "ACCEPTEDı", "Rejectedé", "WaıtıngInActıon", "ACCEPTED١", "WaitingFraudChecK", "Accepted ",
        "Waiting–In–Action", "Accepted\U0001F600", "CLOſED", "ACCEPTEDſ", "İnTransit"
    ];

    // The patterns as PostgreSQL reads them (Npgsql writes Regex.IsMatch as ~ and ~* with the pattern unchanged) give the
    // invoice's answer too.
    public static TheoryData<string> DatabaseStatuses()
    {
        var data = new TheoryData<string>();
        foreach (var line in LineStatuses) data.Add(line ?? "");
        return data;
    }

    [PostgreSqlTheory]
    [MemberData(nameof(DatabaseStatuses))]
    public async Task PostgreSQL_reads_the_patterns_as_the_invoice_rule(string line)
    {
        // Only the server's regex engine is under test: the maintenance database answers it, so the test never races the
        // creation of the test database (a fresh database is made by the fixture, which this class does not use).
        var target = new Npgsql.NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")) { Database = "postgres" };
        await using var connection = new Npgsql.NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT @line ~ @foreign, @line ~* @accepted, @line ~* @refused", connection);
        command.Parameters.AddWithValue("line", line);
        command.Parameters.AddWithValue("foreign", AcceptedReturnPolicy.ForeignLetterPattern);
        command.Parameters.AddWithValue("accepted", AcceptedReturnPolicy.AcceptedLinePattern);
        command.Parameters.AddWithValue("refused", AcceptedReturnPolicy.RefusedLinePattern);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var (foreign, accepted, refused) = (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
        var status = MarketplaceReturnStatus.Canonicalize(line);
        Assert.Equal(status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed, !foreign && accepted);
        Assert.Equal(status is ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled or ReturnClaimStatus.ActionRequired, !foreign && refused);
    }

    public static TheoryData<ReturnClaimStatus, string?> Combinations()
    {
        var data = new TheoryData<ReturnClaimStatus, string?>();
        foreach (var claim in Enum.GetValues<ReturnClaimStatus>())
            foreach (var line in LineStatuses) data.Add(claim, line);
        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void The_database_condition_is_the_invoice_rule(ReturnClaimStatus claim, string? line)
    {
        var inDatabase = AcceptedReturnPolicy.LineCountsInDatabase.Compile();
        var row = new AcceptedReturnPolicy.ClaimLine { ClaimStatus = claim, RawStatus = line, Quantity = 1m };
        // The condition runs as SQL, where upper() follows the database's collation (en_US.utf8 on live, C here), not the
        // culture of the process (tr-TR on the owner's machine would turn i into İ); compiled here, it runs invariant.
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { Assert.Equal(ReturnDecisionLinePolicy.StockDispositionAllowed(claim, line), inDatabase(row)); }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.Equal(ReturnDecisionLinePolicy.StockDispositionAllowed(claim, line), AcceptedReturnPolicy.LineCounts(claim, line));
    }

    // Canonicalize reads its words from one table now (so the database condition can list them); every word still reads
    // as it did.
    [Fact]
    public void Every_status_word_reads_as_before()
    {
        var expected = new Dictionary<string, ReturnClaimStatus>
        {
            ["Created"] = ReturnClaimStatus.Requested,
            ["NewRequest"] = ReturnClaimStatus.Requested,
            ["AwaitingPreApproval"] = ReturnClaimStatus.ActionRequired,
            ["WaitingInAction"] = ReturnClaimStatus.ActionRequired,
            ["AwaitingAction"] = ReturnClaimStatus.ActionRequired,
            ["InAnalysis"] = ReturnClaimStatus.ActionRequired,
            ["WaitingFraudCheck"] = ReturnClaimStatus.ActionRequired,
            ["WaitingForSellerAction"] = ReturnClaimStatus.ActionRequired,
            ["Requested"] = ReturnClaimStatus.ActionRequired,
            ["WaitingForShipment"] = ReturnClaimStatus.AwaitingShipment,
            ["WaitingInCargo"] = ReturnClaimStatus.InTransit,
            ["InTransit"] = ReturnClaimStatus.InTransit,
            ["ReturnInTransit"] = ReturnClaimStatus.InTransit,
            ["Shipped"] = ReturnClaimStatus.InTransit,
            ["Accepted"] = ReturnClaimStatus.Approved,
            ["Approved"] = ReturnClaimStatus.Approved,
            ["Rejected"] = ReturnClaimStatus.Rejected,
            ["Unresolved"] = ReturnClaimStatus.Disputed,
            ["InDispute"] = ReturnClaimStatus.Disputed,
            ["Disputed"] = ReturnClaimStatus.Disputed,
            ["Completed"] = ReturnClaimStatus.Completed,
            ["Refunded"] = ReturnClaimStatus.Completed,
            ["Cancelled"] = ReturnClaimStatus.Cancelled,
            ["Canceled"] = ReturnClaimStatus.Cancelled,
            ["Open"] = ReturnClaimStatus.Approved,
            ["Closed"] = ReturnClaimStatus.Completed,
            ["Declined"] = ReturnClaimStatus.Rejected,
            ["waiting_in_cargo"] = ReturnClaimStatus.InTransit,
            ["SomethingNew"] = ReturnClaimStatus.Unknown,
            [""] = ReturnClaimStatus.Unknown
        };
        foreach (var (word, status) in expected) Assert.Equal(status, MarketplaceReturnStatus.Canonicalize(word));
        Assert.Null(Record.Exception(() => MarketplaceReturnStatus.Canonicalize(null)));
        Assert.Equal(ReturnClaimStatus.Unknown, MarketplaceReturnStatus.Canonicalize(null));
        Assert.Equal(ReturnClaimStatus.InTransit, MarketplaceReturnStatus.Canonicalize("Created", "https://kargo.example/1"));
        Assert.Equal(ReturnClaimStatus.Requested, MarketplaceReturnStatus.Canonicalize("NewRequest", "https://kargo.example/1"));
        Assert.Equal(27, MarketplaceReturnStatus.WordsOf(Enum.GetValues<ReturnClaimStatus>()).Length);
        Assert.Equal(["ACCEPTED", "APPROVED", "CLOSED", "COMPLETED", "OPEN", "REFUNDED"], MarketplaceReturnStatus.WordsOf(ReturnClaimStatus.Approved, ReturnClaimStatus.Completed));
    }

    // The combinations live had on 15 Nov 2026 (claim status / line status, read-only SQL): the old report rule and the
    // invoice's agree on every one, so no figure moves on live.
    [Theory]
    [InlineData(ReturnClaimStatus.Approved, "Accepted", true)]
    [InlineData(ReturnClaimStatus.Completed, null, true)]
    [InlineData(ReturnClaimStatus.InTransit, "Created", false)]
    [InlineData(ReturnClaimStatus.Rejected, "Rejected", false)]
    [InlineData(ReturnClaimStatus.Requested, "Created", false)]
    public void The_live_combinations_count_as_before(ReturnClaimStatus claim, string? line, bool returned)
    {
        Assert.Equal(returned, LegacyAcceptedReturnRule.Counts(claim, line));
        Assert.Equal(returned, LegacyAcceptedReturnRule.NewCounts(claim, line));
    }

    // Where the old report rule answered otherwise, the report now gives the invoice's answer.
    [Theory]
    // Shopify's words: OPEN is an approved return, CLOSED a finished one, on any claim.
    [InlineData(ReturnClaimStatus.Approved, "Open", false, true)]
    [InlineData(ReturnClaimStatus.Requested, "Open", false, true)]
    [InlineData(ReturnClaimStatus.Unknown, "CLOSED", false, true)]
    // A line still on its way, disputed or with a word the panel does not know follows its approved claim.
    [InlineData(ReturnClaimStatus.Approved, "WaitingInCargo", false, true)]
    [InlineData(ReturnClaimStatus.Completed, "InTransit", false, true)]
    [InlineData(ReturnClaimStatus.Approved, "Unresolved", false, true)]
    [InlineData(ReturnClaimStatus.Completed, "SomethingNew", false, true)]
    [InlineData(ReturnClaimStatus.Approved, "Created", false, true)]
    [InlineData(ReturnClaimStatus.Approved, " ", false, true)]
    // An accepted word written with separators or spaces is still that word.
    [InlineData(ReturnClaimStatus.Requested, "Accepted ", false, true)]
    // A cancelled claim takes nothing back, whatever its lines say.
    [InlineData(ReturnClaimStatus.Cancelled, "Accepted", true, false)]
    [InlineData(ReturnClaimStatus.Cancelled, "Refunded", true, false)]
    public void Where_the_rules_differed_the_report_gives_the_invoice_answer(ReturnClaimStatus claim, string? line, bool before, bool after)
    {
        Assert.Equal(before, LegacyAcceptedReturnRule.Counts(claim, line));
        Assert.Equal(after, LegacyAcceptedReturnRule.NewCounts(claim, line));
    }
}

// The overview's and the reports' accepted-return rule before 15 Nov 2026 (FATURA-FINANS-2), kept only as the reference
// the equivalence tests (LegacyDashboardPerformanceQueries, LegacyReportQueries) and the parity tests compare with.
internal static class LegacyAcceptedReturnRule
{
    // A return claim took its units off the sale once accepted...
    internal static readonly ReturnClaimStatus[] ClaimStatuses = [ReturnClaimStatus.Approved, ReturnClaimStatus.Completed];
    // ...unless the line carried its own word: then only these (upper case) counted, on any claim, a cancelled one too.
    internal static readonly string[] LineRawStatuses = ["ACCEPTED", "APPROVED", "COMPLETED", "REFUNDED"];

    // The old SQL condition for a claim that is no unrefunded exchange.
    internal static bool Counts(ReturnClaimStatus claim, string? line) =>
        string.IsNullOrEmpty(line) ? ClaimStatuses.Contains(claim) : LineRawStatuses.Contains(line.ToUpperInvariant());

    // The rule now (AcceptedReturnPolicy): a cancelled claim is left out, its lines decided by the invoice's function.
    internal static bool NewCounts(ReturnClaimStatus claim, string? line) =>
        claim != ReturnClaimStatus.Cancelled && AcceptedReturnPolicy.LineCounts(claim, line);
}
