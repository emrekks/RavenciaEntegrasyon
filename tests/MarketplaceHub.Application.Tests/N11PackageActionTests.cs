using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.N11;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11PackageActionTests
{
    [Fact]
    public void PickingTurnsTheTrendyolShapedBodyIntoN11LineApproval()
    {
        var body = N11PackageActionMapper.Body("PICKING", """{"lines":[{"lineId":426659152,"quantity":2},{"lineId":426659151,"quantity":1}],"params":{},"status":"Picking"}""");

        using var json = JsonDocument.Parse(body!);
        Assert.Equal("Picking", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(new long[] { 426659152, 426659151 }, json.RootElement.GetProperty("lines").EnumerateArray().Select(x => x.GetProperty("lineId").GetInt64()).ToArray());
        Assert.False(json.RootElement.GetProperty("lines")[0].TryGetProperty("quantity", out _));
    }

    [Fact]
    public void SplitsBecomeN11SplitGroups()
    {
        var single = N11PackageActionMapper.Body("SPLIT", """{"orderLineIds":[448627086,"448627087"],"shouldKeepPreviousStatus":false}""");
        var multi = N11PackageActionMapper.Body("MULTI_SPLIT", """{"splitGroups":[{"orderLineIds":[1]},{"orderLineIds":[2,3]}]}""");

        Assert.Equal("""{"splitGroups":[{"orderLineIds":[448627086,448627087]}]}""", single);
        Assert.Equal("""{"splitGroups":[{"orderLineIds":[1]},{"orderLineIds":[2,3]}]}""", multi);
    }

    [Theory]
    [InlineData("CANCEL_ITEMS")]
    [InlineData("CHANGE_CARGO_PROVIDER")]
    [InlineData("TRACKING_NUMBER")]
    public void ActionsWithoutAnN11ApiAreNotSent(string action) =>
        Assert.Null(N11PackageActionMapper.Body(action, """{"lines":[{"lineId":1}]}"""));

    [Fact]
    public void EmptyPickingIsRejected() =>
        Assert.Throws<JsonException>(() => N11PackageActionMapper.Body("PICKING", """{"lines":[],"status":"Picking"}"""));

    [Fact]
    public void RefusedLinesCarryTheN11Reasons()
    {
        using var mixed = JsonDocument.Parse("""{"content":[{"lineId":1,"status":"SUCCESS","reasons":"Başarıyla tamamlandı."},{"lineId":2,"status":"FAIL","reasons":"Statü uygun değil."}]}""");
        using var allGood = JsonDocument.Parse("""{"content":[{"lineId":1,"status":"SUCCESS","reasons":"Başarıyla tamamlandı."}]}""");
        using var split = JsonDocument.Parse("""{"code":200,"message":"success"}""");
        using var splitRefused = JsonDocument.Parse("""{"code":400,"message":"Created statülü siparişler bölünemez."}""");

        Assert.Equal("2 satırı: Statü uygun değil.", N11PackageActionMapper.RefusedLines(mixed.RootElement));
        Assert.Null(N11PackageActionMapper.RefusedLines(allGood.RootElement));
        Assert.Null(N11PackageActionMapper.RefusedLines(split.RootElement));
        Assert.Equal("Created statülü siparişler bölünemez.", N11PackageActionMapper.RefusedLines(splitRefused.RootElement));
    }
}
