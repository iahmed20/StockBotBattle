using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BrokeragePlatform.Migrations
{
    /// <inheritdoc />
    public partial class SubmissionTickProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastTickAt",
                table: "StrategySubmissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TicksProcessed",
                table: "StrategySubmissions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTickAt",
                table: "StrategySubmissions");

            migrationBuilder.DropColumn(
                name: "TicksProcessed",
                table: "StrategySubmissions");
        }
    }
}
