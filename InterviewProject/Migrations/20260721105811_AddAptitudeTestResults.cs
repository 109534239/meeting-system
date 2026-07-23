using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InterviewProject.Migrations
{
    /// <inheritdoc />
    public partial class AddAptitudeTestResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AptitudeTestResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ResumeId = table.Column<int>(type: "integer", nullable: false),
                    AnswersJson = table.Column<string>(type: "text", nullable: false),
                    StressToleranceScore = table.Column<double>(type: "double precision", nullable: false),
                    TeamworkScore = table.Column<double>(type: "double precision", nullable: false),
                    ProactivenessScore = table.Column<double>(type: "double precision", nullable: false),
                    ReliabilityScore = table.Column<double>(type: "double precision", nullable: false),
                    CommunicationScore = table.Column<double>(type: "double precision", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AptitudeTestResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AptitudeTestResults_Resume_ResumeId",
                        column: x => x.ResumeId,
                        principalTable: "Resume",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AptitudeTestResults_ResumeId",
                table: "AptitudeTestResults",
                column: "ResumeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AptitudeTestResults");
        }
    }
}