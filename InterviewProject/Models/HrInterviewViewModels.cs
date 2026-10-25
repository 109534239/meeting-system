namespace InterviewProject.Models
{
    // HR「面試管理」頁用的資料：等待安排面試的職缺 / 已安排的會議室 / 面試後的錄取結果

    /// <summary>已下架、履歷全部審完、可以由HR安排面試的職缺</summary>
    public class HrReadyJobVm
    {
        public Job Job { get; set; } = null!;
        public List<Resume> ApprovedResumes { get; set; } = new();   // 已通過的求職者（預設全選）
        public List<Employee> Staff { get; set; } = new();           // 該職缺部門的所有職員（預設全選）
    }

    /// <summary>已建立的面試會議室</summary>
    public class HrRoomVm
    {
        public Room Room { get; set; } = null!;
        public List<Resume> Candidates { get; set; } = new();        // 目前在房間裡的求職者
        public List<Employee> StaffInRoom { get; set; } = new();     // 目前在房間裡的職員
        public int? HostEmployeeId { get; set; }                      // 主持人（只會有一位）

        // 面試開始前才能修改，修改表單用的候選名單
        public List<Resume> ApprovedResumes { get; set; } = new();
        public List<Employee> DeptStaff { get; set; } = new();

        // 面試結束後的結果（給HR看誰被主持人定案、職員評分進度）
        public Dictionary<int, int> EvalCountByResume { get; set; } = new();
        public int StaffCount { get; set; }
    }

    public class HrInterviewVm
    {
        public List<HrReadyJobVm> ReadyJobs { get; set; } = new();
        public List<HrRoomVm> Rooms { get; set; } = new();
    }

    /// <summary>建立／修改會議室共用的表單資料（_HrMeetingForm 局部檢視用）</summary>
    public class HrMeetingFormVm
    {
        public string Uid { get; set; } = "";                 // 讓同一頁多個表單的 id 不重複
        public string Action { get; set; } = "";              // HrCreateMeeting / HrUpdateMeeting
        public int? JobId { get; set; }
        public int? RoomId { get; set; }
        public DateTime ScheduledAt { get; set; }
        public List<Resume> Resumes { get; set; } = new();
        public HashSet<int> SelectedResumeIds { get; set; } = new();
        public List<Employee> Staff { get; set; } = new();
        public HashSet<int> SelectedStaffIds { get; set; } = new();
        public int? HostId { get; set; }
        public string SubmitText { get; set; } = "";
    }
}
