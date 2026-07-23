using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewProject.Migrations
{
    /// <inheritdoc />
    public partial class AddInterviewStatusFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdmissionResult",
                table: "Resume",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InterviewStatus",
                table: "Resume",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdmissionResult",
                table: "Resume");

            migrationBuilder.DropColumn(
                name: "InterviewStatus",
                table: "Resume");
        }
    }
}
