namespace InterviewProject.Models
{
    public class AptitudeQuestion
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public string Dimension { get; set; } = ""; // 內部用，不顯示給求職者
    }

    // 固定的 15 題適性測驗題庫（1~5 分李克特量表：1非常不同意 ~ 5非常同意）
    // 涵蓋 5 個職場常見構面，每個構面 3 題
    public static class AptitudeTestBank
    {
        public const string DimStress = "StressTolerance";
        public const string DimTeamwork = "Teamwork";
        public const string DimProactive = "Proactiveness";
        public const string DimReliability = "Reliability";
        public const string DimCommunication = "Communication";

        public static readonly List<AptitudeQuestion> Questions = new()
        {
            new() { Id = 1,  Dimension = DimStress,        Text = "面對緊迫的截止日期，我仍能維持穩定的工作品質。" },
            new() { Id = 2,  Dimension = DimStress,        Text = "遇到突發狀況時，我會先冷靜分析，而不是急著反應。" },
            new() { Id = 3,  Dimension = DimStress,        Text = "即使同時有多項任務壓在身上，我也能安排優先順序完成。" },

            new() { Id = 4,  Dimension = DimTeamwork,      Text = "即使意見和同事不同，我也願意理性溝通、尋求共識。" },
            new() { Id = 5,  Dimension = DimTeamwork,      Text = "我樂於在團隊需要時，主動補位協助其他成員。" },
            new() { Id = 6,  Dimension = DimTeamwork,      Text = "我認為團隊的成果比個人表現更重要。" },

            new() { Id = 7,  Dimension = DimProactive,     Text = "遇到不熟悉的問題，我會主動查資料或請教他人，而不是等待指示。" },
            new() { Id = 8,  Dimension = DimProactive,     Text = "我常常會思考如何把現有的工作流程做得更好。" },
            new() { Id = 9,  Dimension = DimProactive,     Text = "發現問題時，我傾向主動提出並嘗試解決，而非視而不見。" },

            new() { Id = 10, Dimension = DimReliability,   Text = "答應要完成的事情，我會盡全力如期做到。" },
            new() { Id = 11, Dimension = DimReliability,   Text = "即使沒有人在旁監督，我也會確實完成份內工作。" },
            new() { Id = 12, Dimension = DimReliability,   Text = "犯錯時，我會主動承認並提出改善方式，而不是找藉口。" },

            new() { Id = 13, Dimension = DimCommunication, Text = "我能清楚地把自己的想法表達給不同背景的人理解。" },
            new() { Id = 14, Dimension = DimCommunication, Text = "接收到指示不清楚時，我會主動提出確認，而不是自行猜測。" },
            new() { Id = 15, Dimension = DimCommunication, Text = "我會留意對方的反應，適時調整自己的溝通方式。" },
        };
    }

    public class AptitudeAnswerDto
    {
        public int QuestionId { get; set; }
        public int Score { get; set; } // 1~5
    }
}
