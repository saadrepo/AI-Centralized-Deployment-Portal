using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeploymentPortal.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeploymentMode",
                table: "Deployments",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedState",
                table: "Deployments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SiteHostName",
                table: "Deployments",
                type: "nvarchar(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SitePort",
                table: "Deployments",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeploymentMode",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "ExpectedState",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "SiteHostName",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "SitePort",
                table: "Deployments");
        }
    }
}
