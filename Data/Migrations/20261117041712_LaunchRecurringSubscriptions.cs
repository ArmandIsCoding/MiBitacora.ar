using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiBitacora.Data.Migrations
{
    /// <inheritdoc />
    public partial class LaunchRecurringSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AcceptedUtc",
                table: "BillingSubscriptions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancellationRequestedUtc",
                table: "BillingSubscriptions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonthlyCredits",
                table: "BillingSubscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 200);

            migrationBuilder.AddColumn<string>(
                name: "PayerEmail",
                table: "BillingSubscriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TermsVersion",
                table: "BillingSubscriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedUtc",
                table: "BillingPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FiscalInvoiceIssuedUtc",
                table: "BillingPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiscalInvoiceReference",
                table: "BillingPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RefundedCents",
                table: "BillingPayments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcceptedTermsUtc",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcceptedTermsVersion",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiUsageReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PeriodEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Credits = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageReservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiUsageReservations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BillingAudit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VerifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VerificationHash = table.Column<string>(type: "TEXT", nullable: true),
                    VerificationExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Resolution = table.Column<string>(type: "TEXT", nullable: false),
                    PaymentId = table.Column<string>(type: "TEXT", nullable: true),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RefundApprovedByAdmin = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TransactionalEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionalEmails", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageReservations_UserId_PeriodStartUtc",
                table: "AiUsageReservations",
                columns: new[] { "UserId", "PeriodStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingAudit_CreatedUtc",
                table: "BillingAudit",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BillingAudit_Type_Reference",
                table: "BillingAudit",
                columns: new[] { "Type", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequests_Code",
                table: "CustomerRequests",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequests_Email_CreatedUtc",
                table: "CustomerRequests",
                columns: new[] { "Email", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequests_Status_CreatedUtc",
                table: "CustomerRequests",
                columns: new[] { "Status", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequests_UserId_Kind_PaymentId",
                table: "CustomerRequests",
                columns: new[] { "UserId", "Kind", "PaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionalEmails_Key",
                table: "TransactionalEmails",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransactionalEmails_SentUtc_NextAttemptUtc",
                table: "TransactionalEmails",
                columns: new[] { "SentUtc", "NextAttemptUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiUsageReservations");

            migrationBuilder.DropTable(
                name: "BillingAudit");

            migrationBuilder.DropTable(
                name: "CustomerRequests");

            migrationBuilder.DropTable(
                name: "TransactionalEmails");

            migrationBuilder.DropColumn(
                name: "AcceptedUtc",
                table: "BillingSubscriptions");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedUtc",
                table: "BillingSubscriptions");

            migrationBuilder.DropColumn(
                name: "MonthlyCredits",
                table: "BillingSubscriptions");

            migrationBuilder.DropColumn(
                name: "PayerEmail",
                table: "BillingSubscriptions");

            migrationBuilder.DropColumn(
                name: "TermsVersion",
                table: "BillingSubscriptions");

            migrationBuilder.DropColumn(
                name: "ApprovedUtc",
                table: "BillingPayments");

            migrationBuilder.DropColumn(
                name: "FiscalInvoiceIssuedUtc",
                table: "BillingPayments");

            migrationBuilder.DropColumn(
                name: "FiscalInvoiceReference",
                table: "BillingPayments");

            migrationBuilder.DropColumn(
                name: "RefundedCents",
                table: "BillingPayments");

            migrationBuilder.DropColumn(
                name: "AcceptedTermsUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AcceptedTermsVersion",
                table: "AspNetUsers");
        }
    }
}
