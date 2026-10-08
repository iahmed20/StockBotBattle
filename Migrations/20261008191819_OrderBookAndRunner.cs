using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BrokeragePlatform.Migrations
{
    /// <inheritdoc />
    public partial class OrderBookAndRunner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "StrategySubmissions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "StrategySubmissions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FinishedAt",
                table: "StrategySubmissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Log",
                table: "StrategySubmissions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAt",
                table: "StrategySubmissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusReason",
                table: "Orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StrategySubmissionId",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CounterOrderId",
                table: "Executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PriceTickId",
                table: "Executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StrategySubmissions_Status",
                table: "StrategySubmissions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PriceTicks_Symbol_PriceTickId",
                table: "PriceTicks",
                columns: new[] { "Symbol", "PriceTickId" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_AccountId",
                table: "Orders",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_StrategySubmissionId",
                table: "Orders",
                column: "StrategySubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Symbol_Status",
                table: "Orders",
                columns: new[] { "Symbol", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Executions_OrderId",
                table: "Executions",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Executions_PriceTickId",
                table: "Executions",
                column: "PriceTickId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StrategySubmissions_Status",
                table: "StrategySubmissions");

            migrationBuilder.DropIndex(
                name: "IX_PriceTicks_Symbol_PriceTickId",
                table: "PriceTicks");

            migrationBuilder.DropIndex(
                name: "IX_Orders_AccountId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_StrategySubmissionId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_Symbol_Status",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Executions_OrderId",
                table: "Executions");

            migrationBuilder.DropIndex(
                name: "IX_Executions_PriceTickId",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "Error",
                table: "StrategySubmissions");

            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "StrategySubmissions");

            migrationBuilder.DropColumn(
                name: "Log",
                table: "StrategySubmissions");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "StrategySubmissions");

            migrationBuilder.DropColumn(
                name: "StatusReason",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "StrategySubmissionId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CounterOrderId",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "PriceTickId",
                table: "Executions");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "StrategySubmissions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);
        }
    }
}
