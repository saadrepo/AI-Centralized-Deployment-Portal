using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeploymentPortal.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CloudExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UniqueScope = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActorSid = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ProtectedData = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RunId = table.Column<long>(type: "bigint", nullable: true),
                    DeploymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AuditJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudExecutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CloudExecutions_DeploymentId",
                table: "CloudExecutions",
                column: "DeploymentId",
                unique: true,
                filter: "[DeploymentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CloudExecutions_UniqueScope",
                table: "CloudExecutions",
                column: "UniqueScope",
                unique: true,
                filter: "[UniqueScope] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CloudExecutions");
        }
    }
}
