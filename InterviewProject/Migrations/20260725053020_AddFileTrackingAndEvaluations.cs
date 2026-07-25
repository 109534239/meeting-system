using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InterviewProject.Migrations
{
    /// <inheritdoc />
    public partial class AddFileTrackingAndEvaluations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiAnalysisFileName",
                table: "Rooms",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecordingFileName",
                table: "Rooms",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranscriptFileName",
                table: "Rooms",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InterviewEvaluations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoomId = table.Column<int>(type: "integer", nullable: false),
                    ResumeId = table.Column<int>(type: "integer", nullable: false),
                    EvaluatorEmployeeId = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InterviewEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InterviewEvaluations_Employees_EvaluatorEmployeeId",
                        column: x => x.EvaluatorEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InterviewEvaluations_Resume_ResumeId",
                        column: x => x.ResumeId,
                        principalTable: "Resume",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InterviewEvaluations_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InterviewEvaluations_EvaluatorEmployeeId",
                table: "InterviewEvaluations",
                column: "EvaluatorEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_InterviewEvaluations_ResumeId",
                table: "InterviewEvaluations",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_InterviewEvaluations_RoomId",
                table: "InterviewEvaluations",
                column: "RoomId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InterviewEvaluations");

            migrationBuilder.DropColumn(
                name: "AiAnalysisFileName",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "RecordingFileName",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "TranscriptFileName",
                table: "Rooms");
        }
    }
}
