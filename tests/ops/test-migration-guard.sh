#!/usr/bin/env bash
# tests/ops/migration-guard.sh (VERITABANI-DAGITIM-3 and -5) against fake migration files, and against the tree.
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

echo "migration guard"

# Writes a migration whose Up() body is $2 (and an optional marker comment $3) as $work/<id>_Name.cs.
migration() {
  local file="$work/$1.cs"
  cat > "$file" <<EOF
namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    public partial class Fake : Migration
    {
        ${3:-}
        protected override void Up(MigrationBuilder migrationBuilder)
        {
$2
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Down", schema: "s", table: "t");
            migrationBuilder.CreateIndex(name: "IX_down", schema: "s", table: "t", column: "C");
        }
    }
}
EOF
}

guard() { MIGRATION_GUARD_SINCE=20990101000000 bash "$tests_dir/migration-guard.sh" "$work" 2>&1; }
expect_fail() {
  local output status=0
  output="$(guard)" || status=$?
  assert_eq "1" "$status" "$1 fails"
  assert_contains "$output" "$2" "$1 names the problem"
  rm -f -- "$work"/*.cs
}
expect_pass() {
  local output status=0
  output="$(guard)" || status=$?
  assert_eq "0" "$status" "$1 passes"
  rm -f -- "$work"/*.cs
}

migration 20990101000001_Drop '            migrationBuilder.DropColumn(name: "Old", schema: "sales", table: "orders");'
expect_fail "a dropped column" "contract: DropColumn"

migration 20990101000001_Rename '            migrationBuilder.RenameColumn(name: "A", schema: "sales", table: "orders", newName: "B");'
expect_fail "a renamed column" "contract: RenameColumn"

migration 20990101000001_NotNull '            migrationBuilder.AlterColumn<string>(name: "A", schema: "sales", table: "orders", nullable: false,
                oldClrType: typeof(string), oldNullable: true);'
expect_fail "a column made NOT NULL" "contract: AlterColumn"

migration 20990101000001_SqlDrop '            migrationBuilder.Sql("ALTER TABLE sales.orders DROP COLUMN \"Old\"");'
expect_fail "SQL that drops a column" "contract: SQL"

migration 20990101000001_Phase2 '            migrationBuilder.DropColumn(name: "Old", schema: "sales", table: "orders");' \
  '// expand-contract: phase 2 of 20990101000000_StopReadingOld'
expect_pass "a drop marked as phase 2"

migration 20990101000001_Index '            migrationBuilder.CreateIndex(name: "IX_orders_A", schema: "sales", table: "orders", column: "A");'
expect_fail "an index on an existing table" "lock: CreateIndex on existing table orders"

migration 20990101000001_Fk '            migrationBuilder.AddForeignKey(name: "FK_a", schema: "sales", table: "orders", column: "A",
                principalSchema: "sales", principalTable: "b", principalColumn: "Id");'
expect_fail "a foreign key on an existing table" "lock: AddForeignKey"

migration 20990101000001_SqlIndex '            migrationBuilder.Sql("CREATE UNIQUE INDEX \"UX_a\" ON sales.orders (\"A\")");'
expect_fail "SQL CREATE INDEX without CONCURRENTLY" "without CONCURRENTLY"

migration 20990101000001_Concurrent '            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS sales.\"UX_a\"", suppressTransaction: true);
            migrationBuilder.Sql("CREATE UNIQUE INDEX CONCURRENTLY \"UX_a\" ON sales.orders (\"A\") NULLS NOT DISTINCT", suppressTransaction: true);
            migrationBuilder.Sql("ALTER TABLE sales.orders ADD CONSTRAINT ck_a CHECK (\"A\" > 0) NOT VALID");
            migrationBuilder.Sql("ALTER TABLE sales.orders VALIDATE CONSTRAINT ck_a");
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);'
expect_pass "the CONCURRENTLY and NOT VALID patterns"

migration 20990101000001_NewTable '            migrationBuilder.CreateTable(
                name: "fresh",
                schema: "sales",
                columns: table => new { Id = table.Column<Guid>(type: "uuid", nullable: false) });
            migrationBuilder.CreateIndex(name: "IX_fresh_A", schema: "sales", table: "fresh", column: "A");
            migrationBuilder.AddColumn<string>(name: "Note", schema: "sales", table: "orders", nullable: true);'
expect_pass "an index on a table the migration creates, and a nullable column"

migration 20990101000001_LockOk '            migrationBuilder.CreateIndex(name: "IX_tiny_A", schema: "ops", table: "tiny", column: "A");' \
  '// migration-lock-ok: ops.tiny holds a handful of rows'
expect_pass "an index marked as a deliberate exception"

migration 20980101000001_Older '            migrationBuilder.DropColumn(name: "Old", schema: "sales", table: "orders");'
expect_pass "a migration before the cutoff"

# The default cutoff covers no migration that is already on main.
status=0
output="$(bash "$tests_dir/migration-guard.sh" 2>&1)" || status=$?
assert_eq "0" "$status" "the migrations in the tree pass ($output)"

finish "migration guard"
