using System.Runtime.CompilerServices;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 D5 step B2b: the whole test run in one position of Catalog:MirrorModelFields. Unset (the shipped default,
// true): the copies are written. MARKETPLACEHUB_TEST_MIRROR_MODEL_FIELDS=false runs every test with them off, so the
// catalogue, import, editor, Toplu Değer Ata, composer and export classes run in both positions without being duplicated.
// A test that asserts a raw copy column asserts it only while CopiesWritten.
public static class ModelFieldCopiesTestPosition
{
    public static bool CopiesWritten => ModelFieldCopies.Written;

    // The cards and variants a test that asserted the stored rows before B2b reads: the raw rows while the copies are
    // written (the same coverage as before the switch), the model's values (EffectiveCatalog) while they are off.
    public static IQueryable<Product> Cards(AppDbContext db) => CopiesWritten ? db.Products.AsNoTracking() : EffectiveCatalog.Products(db);

    public static IQueryable<ProductVariant> VariantRows(AppDbContext db) => CopiesWritten ? db.ProductVariants.AsNoTracking() : EffectiveCatalog.Variants(db);

    private static int printed;

    // Throws when the run asked for a position and the process holds the other (the fixture calls it at every start). The
    // position line goes to the test output as an xUnit diagnostic message (xunit.runner.json turns them on), to the
    // console and to %TEMP%\marketplacehub-model-field-copies.log.
    public static void Verify(IMessageSink? sink = null)
    {
        var asked = bool.TryParse(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_MIRROR_MODEL_FIELDS"), out var value) ? value : (bool?)null;
        if (Interlocked.Exchange(ref printed, 1) == 0)
        {
            var line = $"[model-field-copies] Catalog:MirrorModelFields = {ModelFieldCopies.Written} (asked: {asked?.ToString() ?? "default"})";
            sink?.OnMessage(new DiagnosticMessage(line));
            Console.WriteLine(line);
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "marketplacehub-model-field-copies.log"), $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        }
        if (asked is { } expected && ModelFieldCopies.Written != expected)
            throw new InvalidOperationException($"Catalog:MirrorModelFields is {ModelFieldCopies.Written}; the run asked for {expected}.");
    }

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (bool.TryParse(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_MIRROR_MODEL_FIELDS"), out var written))
            ModelFieldCopies.Set(written);
    }
}

public sealed class ModelFieldCopiesSettingTests
{
    // The API and the Worker carry the same explicit value (a save in one and a pass in the other must agree on whether
    // the copies are kept); this release ships true.
    [Fact]
    public void TheApiAndTheWorkerCarryTheSameExplicitValue()
    {
        static bool? Read(string project)
        {
            var root = AppContext.BaseDirectory;
            while (root is not null && !File.Exists(Path.Combine(root, "MarketplaceHub.sln"))) root = Path.GetDirectoryName(root);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!, "src", project, "appsettings.json")));
            return json.RootElement.TryGetProperty("Catalog", out var catalog) && catalog.TryGetProperty("MirrorModelFields", out var value) ? value.GetBoolean() : null;
        }
        var api = Read("MarketplaceHub.Api");
        var worker = Read("MarketplaceHub.Worker");
        Assert.NotNull(api);
        Assert.Equal(api, worker);
        Assert.True(api);
    }
}
