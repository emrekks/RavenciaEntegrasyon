using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobReadRecorderTests
{
    [Fact]
    public void Every_record_counts_once_and_the_counts_add_up()
    {
        var recorder = new JobReadRecorder();
        recorder.Add("RETURN", new("C-1", "1001", "Accepted", JobReadOutcomes.Unchanged));
        recorder.Add("RETURN", new("C-1", "1001", "Accepted", JobReadOutcomes.Updated));
        recorder.Add("RETURN", new("C-1", "1001", "Accepted", JobReadOutcomes.Unchanged));
        recorder.Add("RETURN", new("C-2", "1002", "Accepted", JobReadOutcomes.New));
        recorder.Add("RETURN", new("C-3", "1003", null, JobReadOutcomes.Failed, "Bağlı sipariş panelde bulunamadı"));
        recorder.Add("RETURN", new("", null, null, JobReadOutcomes.New));

        var counts = recorder.Counts();
        Assert.Equal(new JobReadCounts(3, 1, 1, 0, 0, 1), counts);
        Assert.Equal(counts.Received, counts.Inserted + counts.Updated + counts.Unchanged + counts.Skipped + counts.Failed);
        Assert.Equal("RETURN", recorder.Kind);
    }

    [Fact]
    public void Merged_slices_keep_problems_first_and_cap_the_list()
    {
        var recorder = new JobReadRecorder();
        for (var i = 0; i < JobReadRecorder.MaxRecords + 20; i++) recorder.Add("ORDER", new($"O-{i}", null, "Created", JobReadOutcomes.Unchanged));
        recorder.Add("ORDER", new("BAD", null, null, JobReadOutcomes.Failed, "x"));
        var previous = new JobReadCounts(2, 1, 0, 1, 0, 0);
        var (counts, kept, omitted) = recorder.MergeWith(previous, [new("P-1", null, null, JobReadOutcomes.New), new("P-2", null, null, JobReadOutcomes.Unchanged)], 0);

        Assert.Equal(2 + JobReadRecorder.MaxRecords + 21, counts.Received);
        Assert.Equal(JobReadRecorder.MaxRecords, kept.Count);
        Assert.Equal("BAD", kept[0].Reference);
        Assert.Equal("P-1", kept[1].Reference);
        Assert.Equal(counts.Received - JobReadRecorder.MaxRecords, omitted);
        Assert.Equal(kept, JobReadRecorder.Deserialize(JobReadRecorder.Serialize(kept)));
        Assert.Empty(JobReadRecorder.Deserialize("not json"));
    }

    [Fact]
    public void The_sentence_says_what_was_read_and_what_came_of_it()
    {
        Assert.Equal("Trendyol'dan 4 iade talebi okundu; hepsi panelde zaten günceldi.", JobReadSentence.Describe("RETURN", "Trendyol", new(4, 0, 0, 4, 0, 0)));
        Assert.Equal("N11'den 3 sipariş okundu: 1 yeni eklendi, 1 güncellendi, 1 işlenemedi.", JobReadSentence.Describe("ORDER", "N11", new(3, 1, 1, 0, 0, 1)));
        Assert.Equal("PTT AVM'den okunacak sipariş gelmedi; panelde değişiklik yapılmadı.", JobReadSentence.Describe("ORDER", "PTT AVM", JobReadCounts.Zero));
        Assert.Equal("Shopify'dan 1 sipariş okundu; panelde zaten günceldi.", JobReadSentence.Describe("ORDER", "Shopify", new(1, 0, 0, 1, 0, 0)));
    }
}
