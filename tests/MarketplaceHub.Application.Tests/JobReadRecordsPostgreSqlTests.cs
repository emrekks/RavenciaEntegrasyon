using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// İş detayı › Çekilen Kayıtlar on PostgreSQL: a product read's stored records page by number in their stored order
// (problems first), filter by problems and search, say honestly how many the job read against how many it kept, stay in
// their tenant, and export to a workbook with as many rows as the list holds, formula text escaped.
[Collection("postgres-integration")]
public sealed class JobReadRecordsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.JobReadSummaries.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task The_stored_records_page_by_number_with_problems_first_and_their_counts()
    {
        var tenant = await TenantAsync();
        var job = await JobAsync(tenant, MarketplaceJobTypes.ProductSync);
        var records = Records(30);
        await SummaryAsync(tenant, job, records, omitted: 0);
        await using var db = fixture.CreateContext();
        var service = new JobReadRecordsService(db);
        async Task<JobReadRecordsPageView> Page(JobReadRecordsQuery query) => (await service.PageAsync(tenant.Id, job.Id, query, CancellationToken.None)).Value!;

        var first = await Page(new JobReadRecordsQuery());
        var second = await Page(new JobReadRecordsQuery(Page: 2));
        var third = await Page(new JobReadRecordsQuery(Page: 3));
        var problems = await Page(new JobReadRecordsQuery(OnlyProblems: true, PageSize: 2));
        var searched = await Page(new JobReadRecordsQuery(Search: "mdl-0007"));
        var byBarcode = await Page(new JobReadRecordsQuery(Search: "86900012"));

        Assert.Equal((25, 5, 0), (first.Items.Count, second.Items.Count, third.Items.Count));
        Assert.Equal((30, 30, 30, 0), (first.TotalCount, first.StoredCount, first.ReceivedCount, first.OmittedCount));
        Assert.Equal(records.Select(x => x.Reference), first.Items.Concat(second.Items).Select(x => x.ModelCode));
        Assert.Equal((1, 25), (first.Page, first.PageSize));
        Assert.Equal(new JobReadRecordCounts(10, 10, 6, 2, 2), first.Counts);
        var failed = first.Items[0];
        Assert.Equal((JobReadOutcomes.Failed, "Kategori eşleşmesi yok", "86900000", 3, 3), (failed.Result, failed.Reason, failed.FirstBarcode, failed.BarcodeCount, failed.VariantCount));
        Assert.Equal((4, 2), (problems.TotalCount, problems.Items.Count));
        Assert.All(problems.Items, item => Assert.Contains(item.Result, new[] { JobReadOutcomes.Failed, JobReadOutcomes.Skipped }));
        Assert.Equal("MDL-0007", Assert.Single(searched.Items).ModelCode);
        Assert.Equal("MDL-0012", Assert.Single(byBarcode.Items).ModelCode);
    }

    [PostgreSqlFact]
    public async Task A_read_that_kept_part_of_its_list_says_so()
    {
        var tenant = await TenantAsync();
        var job = await JobAsync(tenant, MarketplaceJobTypes.ProductSync);
        await SummaryAsync(tenant, job, Records(30), omitted: 124);
        await using var db = fixture.CreateContext();

        var page = (await new JobReadRecordsService(db).PageAsync(tenant.Id, job.Id, new JobReadRecordsQuery(), CancellationToken.None)).Value!;

        Assert.Equal((30, 30, 154, 124), (page.TotalCount, page.StoredCount, page.ReceivedCount, page.OmittedCount));
    }

    [PostgreSqlFact]
    public async Task Another_tenant_and_a_job_that_reads_no_products_get_no_list()
    {
        var tenant = await TenantAsync();
        var stranger = await TenantAsync();
        var job = await JobAsync(tenant, MarketplaceJobTypes.ProductSync);
        var orders = await JobAsync(tenant, MarketplaceJobTypes.OrderReconciliation);
        var queued = await JobAsync(tenant, MarketplaceJobTypes.ProductSync);
        await SummaryAsync(tenant, job, Records(3), omitted: 0);
        await using var db = fixture.CreateContext();
        var service = new JobReadRecordsService(db);

        var foreign = await service.PageAsync(stranger.Id, job.Id, new JobReadRecordsQuery(), CancellationToken.None);
        var foreignExport = await service.ExportAsync(stranger.Id, job.Id, new JobReadRecordsQuery(), CancellationToken.None);
        var notProducts = await service.PageAsync(tenant.Id, orders.Id, new JobReadRecordsQuery(), CancellationToken.None);
        var nothingYet = await service.PageAsync(tenant.Id, queued.Id, new JobReadRecordsQuery(), CancellationToken.None);

        Assert.Equal(("JOB_NOT_FOUND", 404), (foreign.Error!.Code, foreign.Error.Status));
        Assert.Equal(("JOB_NOT_FOUND", 404), (foreignExport.Error!.Code, foreignExport.Error.Status));
        Assert.Equal(400, notProducts.Error!.Status);
        Assert.Equal((0, 0, 0), (nothingYet.Value!.TotalCount, nothingYet.Value.StoredCount, nothingYet.Value.ReceivedCount));
    }

    [PostgreSqlFact]
    public async Task The_export_has_a_row_per_listed_record_with_formula_text_escaped()
    {
        var tenant = await TenantAsync();
        var job = await JobAsync(tenant, MarketplaceJobTypes.ProductSync);
        var records = Records(30).ToList();
        records[29] = records[29] with { Reference = "=1+1", Reason = "@SUM(A1)", Outcome = JobReadOutcomes.Skipped };
        await SummaryAsync(tenant, job, records, omitted: 0);
        await using var db = fixture.CreateContext();
        var service = new JobReadRecordsService(db);

        var page = (await service.PageAsync(tenant.Id, job.Id, new JobReadRecordsQuery(), CancellationToken.None)).Value!;
        var export = (await service.ExportAsync(tenant.Id, job.Id, new JobReadRecordsQuery(), CancellationToken.None)).Value!;
        var problemsPage = (await service.PageAsync(tenant.Id, job.Id, new JobReadRecordsQuery(OnlyProblems: true), CancellationToken.None)).Value!;
        var problems = (await service.ExportAsync(tenant.Id, job.Id, new JobReadRecordsQuery(OnlyProblems: true), CancellationToken.None)).Value!;

        Assert.Matches(@"^product-sync-cekilen-kayitlar-\d{8}\.xlsx$", export.FileName);
        var table = Table(export);
        Assert.Equal(new[] { "Model Kodu", "Barkodlar", "Varyant Sayısı", "Sonuç", "Neden" }, table.Headers);
        Assert.Equal(page.TotalCount, table.Rows.Count);
        Assert.Equal(problemsPage.TotalCount, Table(problems).Rows.Count);
        var first = table.Rows[0];
        Assert.Equal(("MDL-0000", "86900000 (+2)", "3", "Hatalı"), (first.Cells[0], first.Cells[1], first.Cells[2], first.Cells[3]));
        var escaped = table.Rows.Single(row => row.Cells[0] == "'=1+1");
        Assert.Equal("'@SUM(A1)", escaped.Cells[4]);
        Assert.All(table.Rows, row => Assert.Empty(row.FormulaColumns));
    }

    // A product's every variant barcode is kept: the second or third variant's barcode finds its record, and the Excel
    // lists them all. A record stored before (first barcode and a count only) keeps its old cell and is found by that one.
    [PostgreSqlFact]
    public async Task Any_variant_barcode_finds_its_product_and_the_export_lists_them_all()
    {
        var tenant = await TenantAsync();
        var job = await JobAsync(tenant, MarketplaceJobTypes.ProductSync);
        RemoteCatalogVariant Variant(string barcode) => new(barcode, $"SKU-{barcode}", barcode, "MDL-VAR", new Dictionary<string, string>(), false, null, null, null, null, null, "{}");
        var product = new RemoteCatalogProduct("P-VAR", "MDL-VAR", "Ürün", "", null, null, null, null, [],
            [Variant("2000000000011"), Variant(" 2000000000022 "), Variant(""), Variant("2000000000033"), Variant("2000000000011")], "{}");
        var read = CatalogImportService.ProductReadRecord(product, JobReadOutcomes.New, null);
        var old = new JobReadRecord("MDL-OLD", "2000000000044 +2", "3 varyant", JobReadOutcomes.Unchanged);
        await SummaryAsync(tenant, job, [read, old], omitted: 0);
        await using var db = fixture.CreateContext();
        var service = new JobReadRecordsService(db);

        foreach (var barcode in new[] { "2000000000011", "2000000000022", "2000000000033" })
            Assert.Equal("MDL-VAR", Assert.Single((await service.PageAsync(tenant.Id, job.Id, new JobReadRecordsQuery(Search: barcode), CancellationToken.None)).Value!.Items).ModelCode);
        Assert.Equal("MDL-OLD", Assert.Single((await service.PageAsync(tenant.Id, job.Id, new JobReadRecordsQuery(Search: "2000000000044"), CancellationToken.None)).Value!.Items).ModelCode);
        var row = Assert.Single((await service.PageAsync(tenant.Id, job.Id, new JobReadRecordsQuery(Search: "MDL-VAR"), CancellationToken.None)).Value!.Items);
        Assert.Equal(("2000000000011", 5, 5), (row.FirstBarcode, row.BarcodeCount, row.VariantCount));

        var table = Table((await service.ExportAsync(tenant.Id, job.Id, new JobReadRecordsQuery(), CancellationToken.None)).Value!);
        Assert.Equal("2000000000011, 2000000000022, 2000000000033", table.Rows.Single(x => x.Cells[0] == "MDL-VAR").Cells[1]);
        Assert.Equal("2000000000044 (+2)", table.Rows.Single(x => x.Cells[0] == "MDL-OLD").Cells[1]);
    }

    private static XlsxTable Table(JobExportFile file)
    {
        using var content = file.Content;
        return ImportFileReader.ReadXlsxTable(content, 50 * 1024 * 1024);
    }

    // 30 products: 2 failed, 2 skipped, 10 new, 10 updated, 6 unchanged, in the stored order (problems first).
    private static IReadOnlyList<JobReadRecord> Records(int count) => Enumerable.Range(0, count).Select(index => new JobReadRecord(
        $"MDL-{index:0000}",
        index % 2 == 0 ? $"8690{index:0000} +2" : $"8690{index:0000}",
        index % 2 == 0 ? "3 varyant" : "1 varyant",
        index switch { < 2 => JobReadOutcomes.Failed, < 4 => JobReadOutcomes.Skipped, < 14 => JobReadOutcomes.New, < 24 => JobReadOutcomes.Updated, _ => JobReadOutcomes.Unchanged },
        index < 2 ? "Kategori eşleşmesi yok" : index < 4 ? "Panelde eşleşen ürün yok" : null)).ToList();

    private async Task SummaryAsync(Tenant tenant, IntegrationJob job, IReadOnlyList<JobReadRecord> records, int omitted)
    {
        var counts = JobReadRecorder.CountsOf(records);
        await using var db = fixture.CreateContext();
        db.JobReadSummaries.Add(new JobReadSummary
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            JobId = job.Id,
            Kind = "PRODUCT",
            Received = counts.Received + omitted,
            Inserted = counts.Inserted,
            Updated = counts.Updated,
            Unchanged = counts.Unchanged + omitted,
            Skipped = counts.Skipped,
            Failed = counts.Failed,
            Omitted = omitted,
            RecordsJson = JobReadRecorder.Serialize(records),
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<Tenant> TenantAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"read-records-{Guid.NewGuid():N}", DisplayName = "read-records", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<IntegrationJob> JobAsync(Tenant tenant, string jobType)
    {
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var job = new IntegrationJob
        {
            Id = id,
            TenantId = tenant.Id,
            JobType = jobType,
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "h",
            JobDedupKey = $"read-records:{id:N}",
            EffectIdempotencyKey = $"read-records:{id:N}",
            Status = JobStatus.Succeeded,
            AvailableAt = now,
            CorrelationId = $"read-records-{id:N}",
            CreatedAt = now,
            StartedAt = now,
            CompletedAt = now,
            AttemptCount = 1,
            Version = 1
        };
        await using var db = fixture.CreateContext();
        db.IntegrationJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }
}
