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
                name: "Department",
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
                name: "Department",
                table: "FAQReports");

            migrationBuilder.DropColumn(
                name: "InternalNote",
                table: "FAQReports");
        }
    }
}
