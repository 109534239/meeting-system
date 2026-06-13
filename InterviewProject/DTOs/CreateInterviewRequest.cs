using System;

namespace MeetingSystem.DTOs
{
    public class CreateInterviewRequest
    {
        public int ResumeId { get; set; }
        public int ScheduledByEmployeeId { get; set; }
        public string RoomName { get; set; } = string.Empty;

        // 配合需求 3：不帶時區的時間型態 (例如傳入: 2026-05-28 05:00:00)
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
    }
}