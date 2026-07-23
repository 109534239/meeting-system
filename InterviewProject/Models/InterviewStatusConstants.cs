namespace InterviewProject.Models
{
    // Resume.InterviewStatus 可能的值
    public static class InterviewStatusValues
    {
        public const string WaitingSchedule = "等待安排面試";
        public const string Scheduled = "已安排面試";
        public const string InProgress = "面試中";
        public const string Ended = "面試結束";
    }

    // Resume.AdmissionResult 可能的值
    public static class AdmissionResultValues
    {
        public const string Rejected = "未錄取";
        public const string PendingResult = "等待結果中";
        public const string Admitted = "錄取";
    }
}