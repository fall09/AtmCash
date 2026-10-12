using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtmCash.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Deposits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "CassetteMovements",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Denomination",
                table: "CassetteMovements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DepositHoldings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Denomination = table.Column<int>(type: "integer", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepositHoldings", x => x.Id);
                    table.CheckConstraint("CK_DepositHoldings_Count", "\"Count\" >= 0");
                    table.ForeignKey(
                        name: "FK_DepositHoldings_Cassettes_CassetteId",
                        column: x => x.CassetteId,
                        principalTable: "Cassettes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DepositHoldings_CassetteId_Currency_Denomination",
                table: "DepositHoldings",
                columns: new[] { "CassetteId", "Currency", "Denomination" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DepositHoldings");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "CassetteMovements");

            migrationBuilder.DropColumn(
                name: "Denomination",
                table: "CassetteMovements");
        }
    }
}
