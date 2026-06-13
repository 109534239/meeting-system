namespace MeetingSystem.DTOs
{
    public class CompleteInterviewRequest
    {
        public int InterviewScheduleId { get; set; }
        public string ResultNote { get; set; } = string.Empty; // 面試評語
        public int ResultScore { get; set; } // 需求 6：面試分數欄位

        // 需求 5：前端可自由傳入：面試結束、等待結果中、錄取、不錄取 等狀態文字
        public string NextStatus { get; set; } = "面試結束";
    }
}