using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.RegularExpressions;
using static System.Runtime.InteropServices.JavaScript.JSType;

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

            new() { Id = 16, Dimension = DimStress,        Text = "我容易因為一些小事就心神不寧。" },
            new() { Id = 17, Dimension = DimTeamwork,      Text = "我喜歡觀察周遭人之間的互動關係。" },
            new() { Id = 18, Dimension = DimProactive,     Text = "跟別人相處時，我容易順從別人的期望。" },
            new() { Id = 19, Dimension = DimReliability,   Text = "我曾經欺騙過別人。" },
            new() { Id = 20, Dimension = DimCommunication, Text = "我無法忍受事物雜亂無章。" },

            new() { Id = 21, Dimension = DimStress,        Text = "我覺得自己是一個膽大、喜歡冒險的人。" },
            new() { Id = 22, Dimension = DimTeamwork,      Text = "許多人說我腦中似乎永遠有用不完的新點子。" },
            new() { Id = 23, Dimension = DimProactive,     Text = "愈忙碌時，我愈覺得有幹勁。" },
            new() { Id = 24, Dimension = DimReliability,   Text = "在團體中，我會主動爭取擔任領導者" },
            new() { Id = 25, Dimension = DimCommunication, Text = "即使會得罪人，我還是會去指正別人不好的行為。" },

            new() { Id = 26, Dimension = DimStress,        Text = "我喜歡每天的生活都有變化。" },
            new() { Id = 27, Dimension = DimTeamwork,      Text = "要我刻意說些討好他人的話，我會說不出口。" },
            new() { Id = 28, Dimension = DimProactive,     Text = "別人的批評常會讓我花好幾天的時間去平復情緒。" },
            new() { Id = 29, Dimension = DimReliability,   Text = "我從來沒有討厭過任何人。" },
            new() { Id = 30, Dimension = DimCommunication, Text = "為了能考慮周全，我做事情寧可慢一點。" },

            new() { Id = 31, Dimension = DimStress,        Text = "許多人認為我對未來的看法過於悲觀。" },
            new() { Id = 32, Dimension = DimTeamwork,      Text = "我不喜歡別人替我做決定。" },
            new() { Id = 33, Dimension = DimProactive,     Text = "我會刻意當面稱讚別人，讓對方對我留下好印象。" },
            new() { Id = 34, Dimension = DimReliability,   Text = "我不會花心力去做一些需要長時間才能完成的事情。" },
            new() { Id = 35, Dimension = DimCommunication, Text = "看到別人的缺點我會主動告訴他，即使會讓他覺得不舒服。" },

            new() { Id = 36, Dimension = DimStress,        Text = "在團體中，我喜歡被別人推舉為領導者。" },
            new() { Id = 37, Dimension = DimTeamwork,      Text = "我從來沒有抱怨過我的朋友。" },
            new() { Id = 38, Dimension = DimProactive,     Text = "我比一般人更喜歡嘗試新的事物。" },
            new() { Id = 39, Dimension = DimReliability,   Text = "我習慣照著別人告訴我的方法做事。" },
            new() { Id = 40, Dimension = DimCommunication, Text = "我會為了把事做好而特地跟有權勢的人建立好關係" },

            new() { Id = 41, Dimension = DimStress,        Text = "如果有人不同意我的意見， 我會主動提出證據來支持自己的主張。" },
            new() { Id = 42, Dimension = DimTeamwork,      Text = "遇到問題時，我習慣自己一個人獨自去解決。" },
            new() { Id = 43, Dimension = DimProactive,     Text = "違背父母師長的期望會讓我覺得愧疚。" },
            new() { Id = 44, Dimension = DimReliability,   Text = "別人隨口說的幾句話也會刺激我想到不少點子。" },
            new() { Id = 45, Dimension = DimCommunication, Text = "我沒有興趣閱讀太過理論性的書籍。" },

            new() { Id = 46, Dimension = DimStress,        Text = "我不喜歡做內容很固定的工作。" },
            new() { Id = 47, Dimension = DimTeamwork,      Text = "我沒有任何壞習慣。" },
            new() { Id = 48, Dimension = DimProactive,     Text = "對於別人曾經做過令我不舒服的事情，我很難忘記它。" },
            new() { Id = 49, Dimension = DimReliability,   Text = "我曾幫助許多朋友走出低潮。" },
            new() { Id = 50, Dimension = DimCommunication, Text = "我喜歡手頭上同時有好幾件事在忙。" },

            new() { Id = 51, Dimension = DimStress,        Text = "為了認識更多人，我會主動參加各種活動。" },
            new() { Id = 52, Dimension = DimTeamwork,      Text = "我相信事情通常會往好的方向發展。" },
            new() { Id = 53, Dimension = DimProactive,     Text = "我比一般人更容易適應新環境。" },
            new() { Id = 54, Dimension = DimReliability,   Text = "在公開場合我喜歡去觀察別人的一舉一動。" },
            new() { Id = 55, Dimension = DimCommunication, Text = "別人說我是個有擔當的人。" },

            new() { Id = 56, Dimension = DimStress,        Text = "看到一堆散亂的資料，我會花時間把它整理得更有條理。" },
            new() { Id = 57, Dimension = DimTeamwork,      Text = "我是個好勝心強的人。" },
            new() { Id = 58, Dimension = DimProactive,     Text = "發現小瑕疵而沒有修改，我會覺得很不舒服。" },
            new() { Id = 59, Dimension = DimReliability,   Text = "我認為讓別人知道自己的功勞沒什麼不妥。" },
            new() { Id = 60, Dimension = DimCommunication, Text = "我會因為別人認為不值得繼續努力而放棄一件事。" },

            new() { Id = 61, Dimension = DimStress,        Text = "跟別人一起工作會讓我做得更起勁。" },
            new() { Id = 62, Dimension = DimTeamwork,      Text = "旅遊時，我喜歡體驗不同的風土民情勝於觀賞名勝風景。" },
            new() { Id = 63, Dimension = DimProactive,     Text = "不入虎穴焉得虎子， 所以我敢去做許多人不敢做的事。" },
            new() { Id = 64, Dimension = DimReliability,   Text = "我做事情不需要別人一再提醒。" },
            new() { Id = 65, Dimension = DimCommunication, Text = "我常會因為擔心事情做不好而感到有壓力。" },

            new() { Id = 66, Dimension = DimStress,        Text = "我從來沒有跟任何人吵過架。" },
            new() { Id = 67, Dimension = DimTeamwork,      Text = "事情進展不如預期，我會感到不知所措 。" },
            new() { Id = 68, Dimension = DimProactive,     Text = "沒有時間仔細思考就要我採取行動，我會很不放心。" },
            new() { Id = 69, Dimension = DimReliability,   Text = "許多人說我有很多天馬行空的想法。" },
            new() { Id = 70, Dimension = DimCommunication, Text = "不論在那一方面，我都希望自己贏過別人。" },

            new() { Id = 71, Dimension = DimStress,        Text = "我會為了捍衛自己的主張而與別人爭辯。" },
            new() { Id = 72, Dimension = DimTeamwork,      Text = "我常會說出一些讓別人意想不到的答案。" },
            new() { Id = 73, Dimension = DimProactive,     Text = "和別人發生爭執就算事情過了，我心裡還是會有疙瘩。" },
            new() { Id = 74, Dimension = DimReliability,   Text = "別人都說我嘴巴很甜。" },
            new() { Id = 75, Dimension = DimCommunication, Text = "跟別人約定的事，不管喜不喜歡，我都會完成它。" },

            new() { Id = 76, Dimension = DimStress,        Text = "我從來沒有討厭過任何人。" },
            new() { Id = 77, Dimension = DimTeamwork,      Text = "對於團體目標，其他人如果不努力， 我也不願獨自拼命。" },
            new() { Id = 78, Dimension = DimProactive,     Text = "不管對方是誰，只要做出讓我不高興的事，我就會讓他知道。" },
            new() { Id = 79, Dimension = DimReliability,   Text = "幫好朋友掩飾過錯會讓我心裡不安。" },
            new() { Id = 80, Dimension = DimCommunication, Text = "我覺得人生充滿了無奈。" },

            new() { Id = 81, Dimension = DimStress,        Text = "我喜歡閱讀報章雜誌中對於不同國家、社會、政治、宗教的相關評論或省思。" },
            new() { Id = 82, Dimension = DimTeamwork,      Text = "我喜歡嘗試新奇但潛藏危險的活動。" },
            new() { Id = 83, Dimension = DimProactive,     Text = "只要是我認為對的事，我會設法讓別人認同我。" },
            new() { Id = 84, Dimension = DimReliability,   Text = "我不喜歡變化太多的工作。" },
            new() { Id = 85, Dimension = DimCommunication, Text = "我沒有任何壞習慣。" },

            new() { Id = 86, Dimension = DimStress,        Text = "我習慣把桌面上的東西擺得整整齊齊。" },
            new() { Id = 87, Dimension = DimTeamwork,      Text = "當大家討論事情爭論不休時，我會主動出來主持討論。" },
            new() { Id = 88, Dimension = DimProactive,     Text = "跟別人談話時，我會隨時注意對方的反應來調整我說話的內容 。" },
            new() { Id = 89, Dimension = DimReliability,   Text = "在社交場合理我會主動介紹自己。" },
            new() { Id = 90, Dimension = DimCommunication, Text = "回答問題前我會再三思考。" },

            new() { Id = 91, Dimension = DimStress,        Text = "別人跟我談過話後，常說我很瞭解他們的感受。" },
            new() { Id = 92, Dimension = DimTeamwork,      Text = "我喜歡觀賞介紹各地風俗文化的節目。" },
            new() { Id = 93, Dimension = DimProactive,     Text = "我從來不在背後說別人的壞話。" },
            new() { Id = 94, Dimension = DimReliability,   Text = "與人相處時，我比較是附和他人意見的人。" },
            new() { Id = 95, Dimension = DimCommunication, Text = "我會擔心一些別人覺得沒什麼好擔心的事。" },

            new() { Id = 96, Dimension = DimStress,        Text = "我喜歡跟別人說我過去的各種成就。" },
            new() { Id = 97, Dimension = DimTeamwork,      Text = "我喜歡跟一群人共同完成一件事情的感覺。" },
            new() { Id = 98, Dimension = DimProactive,     Text = "我凡事靠自己，非不得已不會開口請別人幫忙。" },
            new() { Id = 99, Dimension = DimReliability,   Text = "我認為不斷追求更高成就是我人生中很重要的事。" },
            new() { Id = 100, Dimension = DimCommunication, Text = "即使沒什麼事要做，我也閒不下來。" },

            new() { Id = 101, Dimension = DimStress,        Text = "我相信自己能承接各種不同的任務。" },
            new() { Id = 102, Dimension = DimTeamwork,      Text = "我喜歡人多的聚會。" },
            new() { Id = 103, Dimension = DimProactive,     Text = "我喜歡去挑戰別人做不到的事情。" },
            new() { Id = 104, Dimension = DimReliability,   Text = "團體氣氛不對時，我會很快感覺出來。" },
            new() { Id = 105, Dimension = DimCommunication, Text = "我習慣遵守父母師長所說的話或指示。" },

            new() { Id = 106, Dimension = DimStress,        Text = "做事只要有一點不完美，我就會覺得懊惱。" },
            new() { Id = 107, Dimension = DimTeamwork,      Text = "遇到不如意的事，我通常不會傷心難過太久。" },
            new() { Id = 108, Dimension = DimProactive,     Text = "我從來沒有跟任何人吵過架。" },
            new() { Id = 109, Dimension = DimReliability,   Text = "一旦投入一件事，還沒達到預期成果我不會罷休。" },
            new() { Id = 110, Dimension = DimCommunication, Text = "如果第二天早上有重要的事，前一天晚上我會睡不好。" },

            new() { Id = 111, Dimension = DimStress,        Text = "我覺得把事情做好比做得快來得重要。" },
            new() { Id = 112, Dimension = DimTeamwork,      Text = "在團體中，我常會告訴別人該做什麼事。" },
            new() { Id = 113, Dimension = DimProactive,     Text = "看到朋友心情不好時，我會主動詢問是不是需要幫忙。" },
            new() { Id = 114, Dimension = DimReliability,   Text = "我喜歡玩需要動腦筋的遊戲。" },
            new() { Id = 115, Dimension = DimCommunication, Text = "不管發生什麼事，我相信好運會站在自己這邊。" },

            new() { Id = 116, Dimension = DimStress,        Text = "我曾經欺騙過別人。" },
            new() { Id = 117, Dimension = DimTeamwork,      Text = "我認為自己會無法忍受退休後無事可做的生活。" },
            new() { Id = 118, Dimension = DimProactive,     Text = "看到不公平的事情，我沒有辦法袖手旁觀。" },
            new() { Id = 119, Dimension = DimReliability,   Text = "我喜歡用不同的方式做事。" },
            new() { Id = 120, Dimension = DimCommunication, Text = "即使不嚴重，我也看不慣別人做出不道德的事。" },

            new() { Id = 121, Dimension = DimStress,        Text = "到一個新地方就要我做調整，覺得很麻煩。" },
            new() { Id = 122, Dimension = DimTeamwork,      Text = "我不喜歡配合別人調整我的做事步調。" },
            new() { Id = 123, Dimension = DimProactive,     Text = "我喜歡嘗試以前沒有吃過的餐館或食物。" },
            new() { Id = 124, Dimension = DimReliability,   Text = "我比較喜歡從事必須自己一個人完成的工作。" },
            new() { Id = 125, Dimension = DimCommunication, Text = "我常號召別人來發起一些活動。" },

            new() { Id = 126, Dimension = DimStress,        Text = "朋友說我很容易說中他們心裡在想什麼。" },
            new() { Id = 127, Dimension = DimTeamwork,      Text = "我不會為了討好別人而低聲下氣。" },
            new() { Id = 128, Dimension = DimProactive,     Text = "我從來不在背後說別人的壞話。" },
            new() { Id = 129, Dimension = DimReliability,   Text = "在團體中跟別人一起做事最能激發我的潛能。" },
            new() { Id = 130, Dimension = DimCommunication, Text = "我喜歡說服別人照我的意見行事。" },

            new() { Id = 131, Dimension = DimStress,        Text = "即使玩同一個遊戲，我也常能想出新的玩法。" },
            new() { Id = 132, Dimension = DimTeamwork,      Text = "在社交場合中，我會覺得無聊。" },
            new() { Id = 133, Dimension = DimProactive,     Text = "我相信我的能力足夠幫助我克服工作上可能面臨的挑戰。" },
            new() { Id = 134, Dimension = DimReliability,   Text = "我常忘記自己該做的事。" },
            new() { Id = 135, Dimension = DimCommunication, Text = "我不喜歡別人搶我的功勞。" },

            new() { Id = 136, Dimension = DimStress,        Text = "我喜歡挑戰權威。" },

        };
    }

    public class AptitudeAnswerDto
    {
        public int QuestionId { get; set; }
        public int Score { get; set; } // 1~5
    }
}
