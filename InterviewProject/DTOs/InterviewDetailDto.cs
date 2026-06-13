using System;

namespace MeetingSystem.DTOs
{
    public class InterviewDetailDto
    {
        public int InterviewId { get; set; }
        public int ResumeId { get; set; }
        public string MemberName { get; set; } = string.Empty; // 跨表查出的應徵者名字
        public string JobTitle { get; set; } = string.Empty;   // 跨表查出的職缺名稱
        public string RoomName { get; set; } = string.Empty;
        public string JitsiRoomName { get; set; } = string.Empty;
        public DateTime? StartAt { get; set; }
        public DateTime? EndAt { get; set; }
        public string CurrentStatus { get; set; } = string.Empty; // 需求 5：從 Resume.Status 撈出來的狀態
        public string? ResultNote { get; set; }
        public int? ResultScore { get; set; } // 需求 6：分數
    }
}