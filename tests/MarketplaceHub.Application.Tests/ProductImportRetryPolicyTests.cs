using System.Text.Json;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-1 retry (16 Nov 2026): the list of failed product models read again one by one, three tries, bounded.
public sealed class ProductImportRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_failed_model_joins_the_list_once_for_its_first_try()
    {
        var entries = ProductImportRetryPolicy.Note([], ["MZ1", "MZ1", " ", "MZ2"], Now, out var added);
        Assert.Equal(["MZ1", "MZ2"], entries.Select(x => x.LookupKey));
        Assert.Equal(2, added.Count);
        Assert.All(entries, entry => Assert.Equal((0, Now.AddMinutes(30)), (entry.Attempts, entry.NextAt)));

        var again = ProductImportRetryPolicy.Note(entries, ["MZ1"], Now.AddMinutes(5), out var addedAgain);
        Assert.Empty(addedAgain);
        Assert.Equal(entries, again);
    }

    [Fact]
    public void The_list_is_bounded_and_overdue_tries_are_given_up()
    {
        var many = ProductImportRetryPolicy.Note([], Enumerable.Range(0, 60).Select(index => $"MZ{index}"), Now, out _);
        Assert.Equal(ProductImportRetryPolicy.MaxTracked, many.Count);
        var later = ProductImportRetryPolicy.Note(many, ["NEW"], Now.AddMinutes(30) + ProductImportRetryPolicy.StaleAfter, out var added);
        Assert.Equal(["NEW"], later.Select(x => x.LookupKey));
        Assert.Single(added);
    }

    [Fact]
    public void A_stored_model_leaves_the_list_and_a_failing_one_leaves_after_its_third_try()
    {
        var entries = ProductImportRetryPolicy.Note([], ["MZ1", "MZ2"], Now, out _);
        var afterStored = ProductImportRetryPolicy.AfterRetry(entries, "MZ1", stored: true, Now, out var none);
        Assert.Null(none);
        Assert.Equal(["MZ2"], afterStored.Select(x => x.LookupKey));

        var list = afterStored;
        list = ProductImportRetryPolicy.AfterRetry(list, "MZ2", stored: false, Now, out var second);
        Assert.Equal((1, Now.AddHours(2)), (second!.Attempts, second.NextAt));
        list = ProductImportRetryPolicy.AfterRetry(list, "MZ2", stored: false, Now, out var third);
        Assert.Equal((2, Now.AddHours(8)), (third!.Attempts, third.NextAt));
        list = ProductImportRetryPolicy.AfterRetry(list, "MZ2", stored: false, Now, out var fourth);
        Assert.Null(fourth);
        Assert.Empty(list);
        Assert.Null(ProductImportRetryPolicy.Serialize(list));
    }

    [Fact]
    public void Each_try_has_its_own_job_and_the_job_reads_only_that_model()
    {
        var connection = Guid.CreateVersion7();
        Assert.NotEqual(ProductImportRetryPolicy.JobDedupKey(connection, "MZ1", 0), ProductImportRetryPolicy.JobDedupKey(connection, "MZ1", 1));
        Assert.True(ProductImportRetryPolicy.JobDedupKey(connection, new string('x', 400), 2).Length <= 256);

        using var payload = JsonDocument.Parse(MarketplaceJobProcessor.ProductImportRetryPayload("""{"full":true,"updateExistingProducts":false}""", connection, "MZ1"));
        Assert.False(payload.RootElement.GetProperty("full").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("updateExistingProducts").GetBoolean());
        Assert.Equal("MZ1", payload.RootElement.GetProperty("productLookup").GetString());
        Assert.True(payload.RootElement.GetProperty(ProductImportRetryPolicy.PayloadFlag).GetBoolean());
        Assert.Equal(connection, payload.RootElement.GetProperty("connectionId").GetGuid());

        Assert.True(ProductImportRetryPolicy.Supports("TRENDYOL"));
        Assert.False(ProductImportRetryPolicy.Supports("N11"));
        Assert.Null(MarketplaceJobProcessor.RetryLookupKey("not json"));
    }
}
