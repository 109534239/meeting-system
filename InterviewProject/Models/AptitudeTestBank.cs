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
            new() { Id = 1,  Dimension = DimStress,        Text = "我喜歡別人公開稱讚我的成就。" },
            new() { Id = 2,  Dimension = DimStress,        Text = "朋友說我很會鼓勵別人。" },
            new() { Id = 3,  Dimension = DimStress,        Text = "當別人談論抽象性或理論性的話題時，我會覺得無趣。" },

            new() { Id = 4,  Dimension = DimTeamwork,      Text = "即使犯了一點小錯（如：交通違規被開罰單），也會讓我感覺慚愧。" },
            new() { Id = 5,  Dimension = DimTeamwork,      Text = "我喜歡到處旅遊去體驗不同的人事物。" },
            new() { Id = 6,  Dimension = DimTeamwork,      Text = "聚會中我喜歡跟不同的人聊天。" },

            new() { Id = 7,  Dimension = DimProactive,     Text = "我會避免去學一些看起來很難的東西。" },
            new() { Id = 8,  Dimension = DimProactive,     Text = "與別人意見不同時，我會試著說服對方接受我的想法。" },
            new() { Id = 9,  Dimension = DimProactive,     Text = "我從來沒有抱怨過我的朋友。" },

            new() { Id = 10, Dimension = DimReliability,   Text = "答應別人的事情， 即使再忙再累，我也會完成它。" },
            new() { Id = 11, Dimension = DimReliability,   Text = "無論從事什麼工作，我都希望是那個行業中的佼佼者。" },
            new() { Id = 12, Dimension = DimReliability,   Text = "對我而言，花時間在思考問題並找出答案，是一件有樂趣的事。" },

            new() { Id = 13, Dimension = DimCommunication, Text = "我比較喜歡跟別人一起完成事情，勝過獨自完成它。" },
            new() { Id = 14, Dimension = DimCommunication, Text = "對於新的事物我通常比別人更快適應。" },
            new() { Id = 15, Dimension = DimCommunication, Text = "我習慣凡事自己做決定，不喜歡別人幫我出意見。" },
        };
    }

    public class AptitudeAnswerDto
    {
        public int QuestionId { get; set; }
        public int Score { get; set; } // 1~5
    }
}
