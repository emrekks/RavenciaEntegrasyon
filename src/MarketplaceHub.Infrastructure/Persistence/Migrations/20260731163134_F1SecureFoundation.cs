using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class F1SecureFoundation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "ops");

        migrationBuilder.EnsureSchema(
            name: "iam");

        migrationBuilder.EnsureSchema(
            name: "integration");

        migrationBuilder.CreateTable(
            name: "audit_logs",
            schema: "ops",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                Action = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                TargetType = table.Column<string>(type: "text", nullable: false),
                TargetId = table.Column<string>(type: "text", nullable: true),
                Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                CorrelationId = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_logs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "feature_flags",
            schema: "ops",
            columns: table => new
            {
                Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_feature_flags", x => x.Key);
            });

        migrationBuilder.CreateTable(
            name: "operational_issues",
            schema: "ops",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                DedupeKey = table.Column<string>(type: "text", nullable: false),
                Code = table.Column<string>(type: "text", nullable: false),
                Summary = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                OccurrenceCount = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operational_issues", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "roles",
            schema: "iam",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_roles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "tenants",
            schema: "iam",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenants", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "users",
            schema: "iam",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                ForcePasswordChange = table.Column<bool>(type: "boolean", nullable: false),
                SessionVersion = table.Column<long>(type: "bigint", nullable: false),
                LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                PasswordHash = table.Column<string>(type: "text", nullable: true),
                SecurityStamp = table.Column<string>(type: "text", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                PhoneNumber = table.Column<string>(type: "text", nullable: true),
                PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "role_claims",
            schema: "iam",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                ClaimType = table.Column<string>(type: "text", nullable: true),
                ClaimValue = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_role_claims", x => x.Id);
                table.ForeignKey(
                    name: "FK_role_claims_roles_RoleId",
                    column: x => x.RoleId,
                    principalSchema: "iam",
                    principalTable: "roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "external_effect_records",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                EffectType = table.Column<string>(type: "text", nullable: false),
                IdempotencyKey = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_external_effect_records", x => x.Id);
                table.ForeignKey(
                    name: "FK_external_effect_records_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "file_assets",
            schema: "ops",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                RelativePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                MimeType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Sha256 = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_file_assets", x => x.Id);
                table.ForeignKey(
                    name: "FK_file_assets_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "inbox_messages",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Source = table.Column<string>(type: "text", nullable: false),
                ExternalMessageId = table.Column<string>(type: "text", nullable: false),
                PayloadHash = table.Column<string>(type: "text", nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inbox_messages", x => x.Id);
                table.ForeignKey(
                    name: "FK_inbox_messages_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "jobs",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                JobType = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                PayloadJson = table.Column<string>(type: "text", nullable: false),
                PayloadVersion = table.Column<int>(type: "integer", nullable: false),
                PayloadHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                JobDedupKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                EffectIdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LeaseTokenHash = table.Column<string>(type: "text", nullable: true),
                LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                LastErrorCode = table.Column<string>(type: "text", nullable: true),
                LastErrorSummary = table.Column<string>(type: "text", nullable: true),
                CorrelationId = table.Column<string>(type: "text", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_jobs", x => x.Id);
                table.ForeignKey(
                    name: "FK_jobs_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "bootstrap_state",
            schema: "iam",
            columns: table => new
            {
                Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                ConfigurationFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_bootstrap_state", x => x.Key);
                table.ForeignKey(
                    name: "FK_bootstrap_state_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_bootstrap_state_users_OwnerUserId",
                    column: x => x.OwnerUserId,
                    principalSchema: "iam",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "recovery_codes",
            schema: "iam",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                CodeDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                InvalidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_recovery_codes", x => x.Id);
                table.ForeignKey(
                    name: "FK_recovery_codes_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "iam",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "tenant_memberships",
            schema: "iam",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Role = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_memberships", x => x.Id);
                table.ForeignKey(
                    name: "FK_tenant_memberships_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_tenant_memberships_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "iam",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "user_claims",
            schema: "iam",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                ClaimType = table.Column<string>(type: "text", nullable: true),
                ClaimValue = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_claims", x => x.Id);
                table.ForeignKey(
                    name: "FK_user_claims_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "iam",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_logins",
            schema: "iam",
            columns: table => new
            {
                LoginProvider = table.Column<string>(type: "text", nullable: false),
                ProviderKey = table.Column<string>(type: "text", nullable: false),
                ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                UserId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_logins", x => new { x.LoginProvider, x.ProviderKey });
                table.ForeignKey(
                    name: "FK_user_logins_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "iam",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_roles",
            schema: "iam",
            columns: table => new
            {
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                RoleId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_roles", x => new { x.UserId, x.RoleId });
                table.ForeignKey(
                    name: "FK_user_roles_roles_RoleId",
                    column: x => x.RoleId,
                    principalSchema: "iam",
                    principalTable: "roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_user_roles_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "iam",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_security",
            schema: "iam",
            columns: table => new
            {
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                TotpState = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                ProtectedTotpSecret = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                EnrollmentExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastAcceptedTimeStep = table.Column<long>(type: "bigint", nullable: true),
                RecoveryBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_security", x => x.UserId);
                table.ForeignKey(
                    name: "FK_user_security_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "iam",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_sessions",
            schema: "iam",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                SessionVersion = table.Column<long>(type: "bigint", nullable: false),
                IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_sessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_user_sessions_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_user_sessions_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "iam",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_tokens",
            schema: "iam",
            columns: table => new
            {
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                LoginProvider = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Value = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_tokens", x => new { x.UserId, x.LoginProvider, x.Name });
                table.ForeignKey(
                    name: "FK_user_tokens_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "iam",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "job_attempts",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                ErrorCode = table.Column<string>(type: "text", nullable: true),
                ErrorSummary = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_job_attempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_job_attempts_jobs_JobId",
                    column: x => x.JobId,
                    principalSchema: "integration",
                    principalTable: "jobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_job_attempts_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_bootstrap_state_OwnerUserId",
            schema: "iam",
            table: "bootstrap_state",
            column: "OwnerUserId");

        migrationBuilder.CreateIndex(
            name: "IX_bootstrap_state_TenantId",
            schema: "iam",
            table: "bootstrap_state",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "IX_external_effect_records_TenantId_EffectType_IdempotencyKey",
            schema: "integration",
            table: "external_effect_records",
            columns: new[] { "TenantId", "EffectType", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_file_assets_TenantId_RelativePath",
            schema: "ops",
            table: "file_assets",
            columns: new[] { "TenantId", "RelativePath" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_inbox_messages_TenantId_Source_ExternalMessageId",
            schema: "integration",
            table: "inbox_messages",
            columns: new[] { "TenantId", "Source", "ExternalMessageId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_job_attempts_JobId_AttemptNumber",
            schema: "integration",
            table: "job_attempts",
            columns: new[] { "JobId", "AttemptNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_job_attempts_TenantId",
            schema: "integration",
            table: "job_attempts",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "IX_jobs_Status_AvailableAt_Priority",
            schema: "integration",
            table: "jobs",
            columns: new[] { "Status", "AvailableAt", "Priority" });

        migrationBuilder.CreateIndex(
            name: "IX_jobs_TenantId_JobType_JobDedupKey",
            schema: "integration",
            table: "jobs",
            columns: new[] { "TenantId", "JobType", "JobDedupKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_operational_issues_DedupeKey",
            schema: "ops",
            table: "operational_issues",
            column: "DedupeKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_recovery_codes_UserId_CodeDigest",
            schema: "iam",
            table: "recovery_codes",
            columns: new[] { "UserId", "CodeDigest" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_role_claims_RoleId",
            schema: "iam",
            table: "role_claims",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "RoleNameIndex",
            schema: "iam",
            table: "roles",
            column: "NormalizedName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_tenant_memberships_TenantId_UserId",
            schema: "iam",
            table: "tenant_memberships",
            columns: new[] { "TenantId", "UserId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_tenant_memberships_UserId",
            schema: "iam",
            table: "tenant_memberships",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_tenants_Code",
            schema: "iam",
            table: "tenants",
            column: "Code",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_user_claims_UserId",
            schema: "iam",
            table: "user_claims",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_user_logins_UserId",
            schema: "iam",
            table: "user_logins",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_user_roles_RoleId",
            schema: "iam",
            table: "user_roles",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "IX_user_sessions_TenantId",
            schema: "iam",
            table: "user_sessions",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "IX_user_sessions_TokenHash",
            schema: "iam",
            table: "user_sessions",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_user_sessions_UserId_State",
            schema: "iam",
            table: "user_sessions",
            columns: new[] { "UserId", "State" });

        migrationBuilder.CreateIndex(
            name: "EmailIndex",
            schema: "iam",
            table: "users",
            column: "NormalizedEmail");

        migrationBuilder.CreateIndex(
            name: "UserNameIndex",
            schema: "iam",
            table: "users",
            column: "NormalizedUserName",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_logs",
            schema: "ops");

        migrationBuilder.DropTable(
            name: "bootstrap_state",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "external_effect_records",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "feature_flags",
            schema: "ops");

        migrationBuilder.DropTable(
            name: "file_assets",
            schema: "ops");

        migrationBuilder.DropTable(
            name: "inbox_messages",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "job_attempts",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "operational_issues",
            schema: "ops");

        migrationBuilder.DropTable(
            name: "recovery_codes",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "role_claims",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "tenant_memberships",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "user_claims",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "user_logins",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "user_roles",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "user_security",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "user_sessions",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "user_tokens",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "jobs",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "roles",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "users",
            schema: "iam");

        migrationBuilder.DropTable(
            name: "tenants",
            schema: "iam");
    }
}
