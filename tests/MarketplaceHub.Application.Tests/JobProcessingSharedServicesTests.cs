using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-9 stage 1 / ISLEYICI-12 (16 Nov 2026): the payload reader, the resumable cursor and the text helpers the job
// processor and the handlers now share read and write exactly what their copies did.
public sealed class JobProcessingSharedServicesTests
{
    [Fact]
    public void The_payload_reader_gives_the_fallback_for_a_missing_value_or_a_body_that_is_not_json()
    {
        const string Payload = """{"full":true,"batchSize":500,"name":"x","off":false}""";
        Assert.True(JobPayloadReader.Boolean(Payload, "full"));
        Assert.False(JobPayloadReader.Boolean(Payload, "missing"));
        Assert.False(JobPayloadReader.Boolean("{not json", "full"));
        Assert.False(JobPayloadReader.BooleanOrDefault(Payload, "off", true));
        Assert.True(JobPayloadReader.BooleanOrDefault(Payload, "missing", true));
        Assert.Equal(250, JobPayloadReader.BoundedInt(Payload, "batchSize", 50, 1, 250));
        Assert.Equal(50, JobPayloadReader.BoundedInt("{not json", "batchSize", 50, 1, 250));
        Assert.Equal("x", JobPayloadReader.Text(Payload, "name"));
        Assert.Null(JobPayloadReader.Text(Payload, "full"));
    }

    [Fact]
    public void The_order_read_request_names_one_order_or_a_list_read_and_refuses_a_body_that_is_not_json()
    {
        var single = JobPayloadReader.OrderRead("""{"externalOrderId":"1001","packageNumber":" P1 ","full":false}""")!;
        Assert.True(single.IsSingleOrder);
        Assert.Equal(("1001", " P1 ", false), (single.ExternalOrderId, single.PackageNumber, single.Full));
        var list = JobPayloadReader.OrderRead("""{"full":true,"externalOrderId":" "}""")!;
        Assert.False(list.IsSingleOrder);
        Assert.True(list.Full);
        Assert.Null(JobPayloadReader.OrderRead("{not json"));
    }

    private sealed record State(string Version, int Page);

    [Fact]
    public void A_resumable_state_is_read_only_when_its_rule_accepts_it_and_saving_moves_the_cursor_version()
    {
        var cursor = new SyncCursor { Id = Guid.CreateVersion7(), TenantId = Guid.CreateVersion7(), ConnectionId = Guid.CreateVersion7(), ResourceType = "X", Version = 3 };
        ResumableCursor.Save(cursor, new State("v1", 2));
        Assert.Equal(4, cursor.Version);
        Assert.Equal(new State("v1", 2), ResumableCursor.Read<State>(cursor.OpaqueCursor, state => state.Version == "v1"));
        Assert.Null(ResumableCursor.Read<State>(cursor.OpaqueCursor, state => state.Version == "v2"));
        Assert.Null(ResumableCursor.Read<State>("{not json", _ => true));
        Assert.Null(ResumableCursor.Read<State>(null, _ => true));
        Assert.Null(ResumableCursor.Read<State>("null", _ => true));
    }

    [Fact]
    public void The_text_helpers_cut_and_hash_as_the_copies_did()
    {
        Assert.Null(TextValues.Truncate(null, 3));
        Assert.Equal("abc", TextValues.Truncate("abcdef", 3));
        Assert.Equal("ab", TextValues.Truncate("ab", 3));
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", TextValues.Sha256Hex("abc"));
    }
}
