using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiBitacora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalReference = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    MonthlyPriceCents = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    CheckoutUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProviderUpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BillingAnchorUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingSubscriptions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BillingWebhooks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingWebhooks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillingPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderInvoiceId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PeriodEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProviderUpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingPayments_BillingSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "BillingSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPayments_SubscriptionId_ProviderInvoiceId",
                table: "BillingPayments",
                columns: new[] { "SubscriptionId", "ProviderInvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPayments_SubscriptionId_ProviderPaymentId",
                table: "BillingPayments",
                columns: new[] { "SubscriptionId", "ProviderPaymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_ExternalReference",
                table: "BillingSubscriptions",
                column: "ExternalReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_Provider_ProviderId",
                table: "BillingSubscriptions",
                columns: new[] { "Provider", "ProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_UserId",
                table: "BillingSubscriptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_UserId_Provider",
                table: "BillingSubscriptions",
                columns: new[] { "UserId", "Provider" },
                unique: true,
                filter: "\"Status\" NOT IN ('cancelled', 'canceled', 'creation_failed')");

            migrationBuilder.CreateIndex(
                name: "IX_BillingWebhooks_ProcessedUtc_NextAttemptUtc",
                table: "BillingWebhooks",
                columns: new[] { "ProcessedUtc", "NextAttemptUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingPayments");

            migrationBuilder.DropTable(
                name: "BillingWebhooks");

            migrationBuilder.DropTable(
                name: "BillingSubscriptions");
        }
    }
}
