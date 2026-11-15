#!/usr/bin/env bash
# Migration safety rules for EF Core migrations newer than the cutoff (VERITABANI-DAGITIM-3 and -5, see
# docs/CI-CD-DEPLOY.md "Migration kuralları"). The previous API and Worker keep running while a deploy migrates,
# so a migration's Up() must be additive and must not hold long locks on tables that already have rows:
#   1. contract: DropColumn, DropTable, RenameColumn, RenameTable, DropSchema, an AlterColumn that makes a column
#      NOT NULL, or SQL DROP COLUMN / DROP TABLE / RENAME COLUMN / ALTER TABLE ... RENAME TO. Allowed only as the
#      second release of an expand-contract change, marked "// expand-contract: phase 2 of <earlier migration>".
#   2. lock: CreateIndex, AddForeignKey or AddCheckConstraint on a table the same migration does not create, SQL
#      CREATE INDEX without CONCURRENTLY, SQL ALTER COLUMN ... [SET DATA] TYPE (a type change rewrites the table under
#      ACCESS EXCLUSIVE), or SQL ALTER TABLE ... ADD CONSTRAINT on a table the migration does not create without
#      NOT VALID (CHECK, FOREIGN KEY) or USING INDEX (UNIQUE, PRIMARY KEY). Use the CONCURRENTLY / NOT VALID + VALIDATE
#      pattern (AddProductVariantModelCodeIndex, ScanFixesSchema), or mark a deliberate exception
#      "// migration-lock-ok: <reason>" (e.g. a varchar widening, which PostgreSQL does without a rewrite).
# Usage: migration-guard.sh [migrations-dir] (MIGRATION_GUARD_SINCE: first migration id checked, default below;
# every migration on main before it predates the rules).
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
migrations_dir="${1:-$script_dir/../../src/MarketplaceHub.Infrastructure/Persistence/Migrations}"
since="${MIGRATION_GUARD_SINCE:-20261115000000}"
[[ "$since" =~ ^[0-9]{14}$ ]] || { echo "MIGRATION_GUARD_SINCE must be a 14-digit migration id." >&2; exit 2; }
[[ -d "$migrations_dir" ]] || { echo "No migrations directory at $migrations_dir." >&2; exit 2; }

checked=0
violations=0
for file in "$migrations_dir"/*.cs; do
  name="${file##*/}"
  [[ "$name" =~ ^([0-9]{14})_.+\.cs$ ]] || continue
  [[ "$name" != *.Designer.cs ]] || continue
  (( 10#${BASH_REMATCH[1]} >= 10#$since )) || continue
  checked=$((checked + 1))
  # The quoted program is Perl; its variables are its own.
  # shellcheck disable=SC2016
  if ! perl -0777 -ne '
    my $file = $ARGV;
    my ($up) = /void\s+Up\s*\((.*?)(?:protected\s+override\s+void\s+Down\s*\(|\z)/s;
    $up //= "";
    my @problems;
    unless (m{//\s*expand-contract:\s*phase 2 of\s+\S}) {
      push @problems, "contract: $1 (old code still runs during the migration; split it into two releases)"
        while $up =~ /\.(DropColumn|DropTable|RenameColumn|RenameTable|DropSchema)\s*\(/g;
      while ($up =~ /\.AlterColumn\s*<[^>]*>\s*\((.*?)\)\s*;/gs) {
        my $args = $1;
        push @problems, "contract: AlterColumn makes a column NOT NULL" if $args =~ /\bnullable:\s*false/ && $args =~ /\boldNullable:\s*true/;
      }
      push @problems, "contract: SQL \"$1\"" while $up =~ /(DROP\s+COLUMN|DROP\s+TABLE|RENAME\s+COLUMN|ALTER\s+TABLE\s+\S+\s+RENAME\s+TO)/gi;
    }
    unless (m{//\s*migration-lock-ok:\s*\S}) {
      my %created;
      $created{$1} = 1 while $up =~ /\.CreateTable\s*\(\s*name:\s*"([^"]+)"/g;
      # A table name as SQL writes it (schema."Name", \"quoted\" in a C# string) reduced to the bare table name.
      my $bare = sub { my $n = shift; $n =~ s/[\\"]//g; $n =~ s/^.*\.//; return $n; };
      $created{$bare->($1)} = 1 while $up =~ /CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([^\s(;]+)/gi;
      while ($up =~ /\.(CreateIndex|AddForeignKey|AddCheckConstraint)\s*\((.*?)\)\s*;/gs) {
        my ($call, $args) = ($1, $2);
        my ($table) = $args =~ /\btable:\s*"([^"]+)"/;
        next if defined $table && $created{$table};
        my $how = $call eq "CreateIndex" ? "CREATE INDEX CONCURRENTLY" : "NOT VALID + VALIDATE CONSTRAINT";
        push @problems, "lock: $call on existing table " . ($table // "?") . " (use $how)";
      }
      while ($up =~ /(CREATE\s+(?:UNIQUE\s+)?INDEX\b(?!\s+CONCURRENTLY)[^"]*)/gi) {
        push @problems, "lock: SQL \"$1\" without CONCURRENTLY";
      }
      while ($up =~ /(ALTER\s+COLUMN\s+\S+\s+(?:SET\s+DATA\s+)?TYPE)\b/gi) {
        push @problems, "lock: SQL \"$1\" rewrites the table under ACCESS EXCLUSIVE (add a new column and backfill it)";
      }
      # The statement runs to the next ";" (the SQL one or the C# one that ends the Sql(...) call).
      while ($up =~ /ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?([^\s;]+)\s+ADD\s+CONSTRAINT\s+[^\s;]+\s+([^;]*)/gi) {
        my ($table, $definition) = ($bare->($1), $2);
        next if $created{$table} || $definition =~ /\bNOT\s+VALID\b|\bUSING\s+INDEX\b/i;
        push @problems, "lock: SQL ADD CONSTRAINT on existing table $table without NOT VALID (add it NOT VALID, then VALIDATE CONSTRAINT)";
      }
    }
    print "$file: $_\n" for @problems;
    exit(@problems ? 1 : 0);
  ' "$file"; then
    violations=$((violations + 1))
  fi
done

if ((violations)); then
  echo "Migration guard: $violations of $checked migration(s) since $since break the rules above (docs/CI-CD-DEPLOY.md \"Migration kuralları\")." >&2
  exit 1
fi
echo "Migration guard: $checked migration(s) since $since follow the expand-contract and lock rules."
