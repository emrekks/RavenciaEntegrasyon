using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WarrantyZeroIsAValue : Migration
    {
        // Garanti Süresi (owner, 15 Nov 2026: "0 ile boş farklı; boşsa doldurmasını istesin ama default 0 zaten"): from this
        // release 0 is stored as 0 and null means "not filled". Until now every writer stored a user's 0 ("garanti yok") as
        // null and every reader read null as 0, so each stored null means 0: this data-only step writes 0 into them, on the
        // cards (catalog.products) and on the model records (catalog.product_models, written through from the cards). No
        // schema change (the column stays a nullable integer). Live before: 1,013 cards and 224 models, all null.
        //
        // While it runs the previous API and Worker still serve (docs/CI-CD-DEPLOY.md "Migration kuralları"): they read 0
        // and null alike, so nothing they show or send changes. Each UPDATE takes only row locks, on a thousand-odd small
        // rows, for milliseconds; no Version, UpdatedAt or change log is written, so no editor holding a loaded version is
        // refused and the model records' "saved after the cards" check is untouched. A 0 the previous code saves between
        // this step and the container swap is stored as null again: the new Worker's ProductWarranty.RepairAsync turns
        // such a card (not a draft) into 0 two minutes after it starts. Repeatable: a second run finds nothing to write.
        private const string Cards = """UPDATE catalog.products SET "WarrantyMonths" = 0 WHERE "WarrantyMonths" IS NULL""";
        private const string Models = """UPDATE catalog.product_models SET "WarrantyMonths" = 0 WHERE "WarrantyMonths" IS NULL""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Cards);
            migrationBuilder.Sql(Models);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately nothing: after Up a 0 the migration wrote cannot be told from a 0 a user saved since (or from a
            // null saved since as "not filled"), so turning 0 back into null would lose information. The previous code
            // reads 0 and null alike, so running it on the migrated data needs no undo.
        }
    }
}
