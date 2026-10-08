using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BrokeragePlatform.Migrations
{
    /// <inheritdoc />
    public partial class CompetitionRounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RoundId",
                table: "StrategySubmissions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TradingAccountId",
                table: "StrategySubmissions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoundId",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoundId",
                table: "Accounts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Rounds",
                columns: table => new
                {
                    RoundId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartingCash = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rounds", x => x.RoundId);
                });

            migrationBuilder.CreateTable(
                name: "RoundEntries",
                columns: table => new
                {
                    RoundEntryId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoundId = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    TradingAccountId = table.Column<int>(type: "integer", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinalEquity = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    FinalRank = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoundEntries", x => x.RoundEntryId);
                    table.ForeignKey(
                        name: "FK_RoundEntries_Rounds_RoundId",
                        column: x => x.RoundId,
                        principalTable: "Rounds",
                        principalColumn: "RoundId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoundEntries_RoundId_AccountId",
                table: "RoundEntries",
                columns: new[] { "RoundId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoundEntries_TradingAccountId",
                table: "RoundEntries",
                column: "TradingAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rounds_Status",
                table: "Rounds",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoundEntries");

            migrationBuilder.DropTable(
                name: "Rounds");

            migrationBuilder.DropColumn(
                name: "RoundId",
                table: "StrategySubmissions");

            migrationBuilder.DropColumn(
                name: "TradingAccountId",
                table: "StrategySubmissions");

            migrationBuilder.DropColumn(
                name: "RoundId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RoundId",
                table: "Accounts");
        }
    }
}
