using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewProject.Migrations
{
    /// <inheritdoc />
    public partial class RemovePhoneFromFAQReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContactType",
                table: "FAQReports");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "FAQReports");

            migrationBuilder.DropColumn(
                name: "PhoneCountryCode",
                table: "FAQReports");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactType",
                table: "FAQReports",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "FAQReports",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PhoneCountryCode",
                table: "FAQReports",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
