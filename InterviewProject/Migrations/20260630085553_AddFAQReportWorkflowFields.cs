using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewProject.Migrations
{
    /// <inheritdoc />
    public partial class AddFAQReportWorkflowFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedRole",
                table: "FAQReports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InternalNote",
                table: "FAQReports",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedRole",
                table: "FAQReports");

            migrationBuilder.DropColumn(
                name: "InternalNote",
                table: "FAQReports");
        }
    }
}
