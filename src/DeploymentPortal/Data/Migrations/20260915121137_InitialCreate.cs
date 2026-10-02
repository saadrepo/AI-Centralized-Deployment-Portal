using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeploymentPortal.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    HostNameOrIp = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OperatingSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ConnectionType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    WinRmPort = table.Column<int>(type: "int", nullable: false),
                    UseSsl = table.Column<bool>(type: "bit", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastConnectionStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Servers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Deployments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<int>(type: "int", nullable: false),
                    ApplicationName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SiteName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    AppPoolName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    SourcePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TargetPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    BackupPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResultMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deployments_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_ServerId",
                table: "Deployments",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_StartedAt",
                table: "Deployments",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_HostNameOrIp_WinRmPort",
                table: "Servers",
                columns: new[] { "HostNameOrIp", "WinRmPort" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Deployments");

            migrationBuilder.DropTable(
                name: "Servers");
        }
    }
}
