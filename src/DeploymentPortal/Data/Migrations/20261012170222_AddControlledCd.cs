using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeploymentPortal.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddControlledCd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CdDeploymentSources",
                columns: table => new
                {
                    PreparationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BuildId = table.Column<int>(type: "int", nullable: false),
                    BuildNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Branch = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Commit = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Artifact = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProtectedSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Expires = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Verification = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AuditJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CdDeploymentSources", x => x.PreparationId);
                    table.ForeignKey(
                        name: "FK_CdDeploymentSources_Deployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "Deployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CdDeploymentSources_DeploymentId",
                table: "CdDeploymentSources",
                column: "DeploymentId",
                unique: true,
                filter: "[DeploymentId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CdDeploymentSources");
        }
    }
}
