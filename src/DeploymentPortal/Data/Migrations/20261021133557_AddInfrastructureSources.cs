using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeploymentPortal.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInfrastructureSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InfrastructureSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Destination = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CredentialReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    ConnectionStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ConnectionMessage = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InfrastructureSources", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureSources_Provider_Destination",
                table: "InfrastructureSources",
                columns: new[] { "Provider", "Destination" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InfrastructureSources");
        }
    }
}
