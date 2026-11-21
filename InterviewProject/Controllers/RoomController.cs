using Microsoft.AspNetCore.Mvc;
using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Services;
using InterviewProject.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace InterviewProject.Controllers
{
    public class RoomController : Controller
    {
        // 錄影只上傳 Gemini 一次：SaveTranscript 上傳後存在這裡，SaveAiAnalysis 直接沿用，用完才刪
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string uri, string name, DateTime at)> _geminiVideoCache = new();
        private static int _liveInFlight = 0;
        // 每個房間、每位求職者被 AI 面試官主動提問的次數（讓 AI 輪流問，不會一直追問同一個人）
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _aiAskCount = new();
        // 整場會議 AI 問過的所有問題（不可重複）、以及上一次問了誰什麼
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _aiQuestions = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Name, string Text)> _aiLast = new();

        // 已產生、但還沒真的說出口的問題（真的說出口才算「問過」，提前準備好的題目被丟掉不算）
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Text, string? Name, bool ModeU)> _aiPending = new();
        // 每位求職者被 AI 要求「自我介紹」的次數（不靠逐字稿，AI 真的說出口就記；最多兩次，絕不會一直回頭問）
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _introAskCount = new();
        private static readonly System.Text.RegularExpressions.Regex IntroBroadRx = new(@"自我介紹|介紹一下自己|介紹自己|簡單介紹|介紹一下吧|介紹你自己|做個介紹");
        // 🎯 伺服器自己記一份「這場會議最近講了什麼」（不靠主持人瀏覽器的 SignalR 收訊，否則收不到就會一直用舊對話出題）
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<AiConvoLine>> _convo = new();
        private static void AddConvo(string code, string name, string role, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var l = _convo.GetOrAdd(code, _ => new List<AiConvoLine>());
            lock (l)
            {
                var t = text.Trim(); if (t.Length > 200) t = t.Substring(0, 200);
                if (l.Count > 0 && l[^1].Name == name && l[^1].Text == t) return;
                l.Add(new AiConvoLine { Name = name ?? "", Role = role ?? "", Text = t });
                if (l.Count > 40) l.RemoveAt(0);
            }
        }


        // ═════════ 即時記錄：會議進行中，每位求職者每答完一題，就即時評估這次回答並記錄 ═════════
        //   會後的個人分析與比較，會把這些即時記錄一起交給 Gemini 當依據（不只是事後重看影片）。
        private static string Clip(string? s, int n) { s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length <= n ? s : s.Substring(0, n) + "…"; }
        public class LiveNote
        {
            public string Name = ""; public string Answer = ""; public string Question = ""; public string AskedWho = "未指定";
            public bool Addressed = true; public int Relevance, Specific, Logic; public string Note = ""; public DateTime At = DateTime.Now;
        }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<LiveNote>> _liveNotes = new();
        private static readonly SemaphoreSlim _noteGate = new(2);

        private void QueueLiveNote(string code, string speaker, string role, string text)
        {
            try
            {
                bool js = string.Equals(role, "jobseeker", StringComparison.OrdinalIgnoreCase) || speaker.StartsWith("求職者");
                var ans = (text ?? "").Trim();
                if (!js || ans.Length < 12) return;
                var name = speaker.Replace("求職者-", "").Trim();
                List<AiConvoLine> ctx = new();
                if (_convo.TryGetValue(code, out var cl)) lock (cl) ctx = cl.TakeLast(7).ToList();
                var ctxText = string.Join("\n", ctx.Select(l => $"{l.Name}：{Clip(l.Text, 80)}"));
                var present = _convo.TryGetValue(code, out var cl2) ? "" : "";
                _ = Task.Run(async () =>
                {
                    await _noteGate.WaitAsync();
                    try
                    {
                        var p = $"下面是面試中最近的對話（由舊到新），最後一段是求職者「{name}」剛回答的內容（語音辨識，可能有錯字）：\n{ctxText}\n\n" +
                                "請判斷：1.「{name}」這次回答的是哪一個問題（簡述）；2.那個問題是問誰（姓名，沒指名或對大家問就寫「未指定」；語音辨識常把名字聽錯，請依發音判斷）；" +
                                "3.相關度、具體度（有無實例/數字/做法）、邏輯（有條理）各 0~10 分；4.一句 25 字內的觀察。\n" +
                                "只輸出一行 JSON：{\"question\":\"\",\"asked\":\"\",\"relevance\":0,\"specific\":0,\"logic\":0,\"note\":\"\"}";
                        p = p.Replace("{name}", name);
                        var raw = await _gemini.AskAsync(p, "你是面試記錄員，只輸出 JSON。", 300);
                        if (string.IsNullOrWhiteSpace(raw)) return;
                        var m = System.Text.RegularExpressions.Regex.Match(raw, @"\{.*\}", System.Text.RegularExpressions.RegexOptions.Singleline);
                        if (!m.Success) return;
                        using var doc = System.Text.Json.JsonDocument.Parse(m.Value);
                        var r = doc.RootElement;
                        int N(string k) { if (r.TryGetProperty(k, out var v)) { if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out var i)) return Math.Clamp(i, 0, 10); if (v.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(v.GetString(), out var j)) return Math.Clamp(j, 0, 10); } return 0; }
                        string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? (v.GetString() ?? "") : "";
                        var asked = S("asked").Trim(); if (string.IsNullOrEmpty(asked)) asked = "未指定";
                        var note = new LiveNote
                        {
                            Name = name,
                            Answer = Clip(ans, 120),
                            Question = Clip(S("question"), 80),
                            AskedWho = asked,
                            Addressed = asked == "未指定" || asked.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(asked, StringComparison.OrdinalIgnoreCase),
                            Relevance = N("relevance"),
                            Specific = N("specific"),
                            Logic = N("logic"),
                            Note = Clip(S("note"), 40)
                        };
                        var list = _liveNotes.GetOrAdd(code, _ => new List<LiveNote>());
                        lock (list) { if (!list.Any(x => x.Name == name && x.Answer == note.Answer)) list.Add(note); }
                    }
                    catch { }
                    finally { _noteGate.Release(); }
                });
            }
            catch { }
        }

        // ═════════ 評分依據（權重）═════════
        // 公平性：只依「回答內容」評分，不含外表、表情、眼神、小動作、口音、畫質與收音（跟履歷評分一樣，只看與職務能力有關的依據）
        private static readonly (string Key, string Title, int Weight, string Basis)[] Rubric =
        {
            ("A", "答題內容與具體度",     35, "有沒有實際做法、數字、案例、工具與成果；空泛口號給低分"),
            ("B", "邏輯結構",             30, "是否緊扣被問的問題、先後有序、因果清楚、能自圓其說"),
            ("C", "與職缺及履歷相關度",   20, "回答與職缺要求、履歷所寫經驗是否相符、是否切題"),
            ("D", "溝通清晰度",           15, "內容是否說得清楚、讓人聽得懂（不含口音、音量、自信感、外表）"),
        };

        public class CandScore { public string Name = ""; public Dictionary<string, int> S = new(); public double Total; public string Summary = "", Strengths = "", Risks = "", Confidence = "", ToConfirm = ""; }

        private string BuildRubricGuidance(string roomCode, string candidate)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("【公平性原則（最重要）】");
            sb.AppendLine("1. 只依「回答內容」評分。不可依外表、表情、眼神、小動作（喝水、擦嘴、推眼鏡、低頭）、口音、音量、自信感、畫面模糊/過亮、回音或收音品質評分，也不要推論情緒、專注或對面試的尊重程度。這些不是求職者能力。");
            sb.AppendLine("2. 看不清、聽不清、講者無法確定、題目是問誰不確定時，寫「無法判斷」，該項不扣分，也不要硬下結論。");
            sb.AppendLine("3. 每個判斷都要附證據：[mm:ss] 加原句。時間一律用「影片時間」mm:ss，不要使用現實時鐘時間。逐字稿是語音辨識，人名與專有名詞常有錯字（例如「吳叔叔」「吳書書」都可能是吳鼠鼠）；與影片聲音不同時以影片聲音為準，並註明差異。");
            sb.AppendLine("4. 先確認每個問題「是誰問的、問誰」。AI 面試官問的題目要寫「AI 面試官」，不可說成職員問的；問別人的題目，他沒回答不算他聽不懂或沒回答，不可扣分。");
            sb.AppendLine("5. 沒有被回答的題目（沒回答、被打斷、題目其實沒被聽見、是問別人的）一律「不計分、不扣分」，只在各題表現寫一行「未回答（不計分）」。不可以因此推論專注度、態度、理解力、反應速度或溝通問題，也不可以把它寫進優缺點、風險或後續關注點。各維度分數只根據「有回答的題目」。");
            sb.AppendLine("6. 「溝通清晰度」只看內容有沒有講清楚、有沒有條理。嚴禁提到口音、發音、語速、音量、聲音、語氣、流暢度；逐字稿裡的錯字（例如把名字或專有名詞聽錯）是語音辨識的問題，不是求職者的問題，不可拿來扣分。也不要描述表情、眼神或動作。");
            sb.AppendLine("7. 不做錄取或不錄取的決定，也不要用「強烈建議／不建議」這類字眼，只列出「建議後續關注點」，最後由人決定。優缺點都要寫，不可通篇讚美或通篇批評。");
            sb.AppendLine();
            sb.AppendLine("【評分依據（四個維度與權重，總分由系統加權計算，你不要自己算總分）】");
            foreach (var r in Rubric) sb.AppendLine($"{r.Key}. {r.Title}（權重 {r.Weight}%）：{r.Basis}");
            sb.AppendLine("每個維度 0~100 分並寫出依據。");
            sb.AppendLine();
            sb.AppendLine($"請依下列結構輸出（繁體中文，不要重複寫同樣的內容；只針對「{candidate}」）：");
            sb.AppendLine("一、各題表現：逐題寫成這種格式（直接寫內容，不要照抄這句說明）：\n   例：[02:15] 職員-王小明問「如何降低成本？」→ 重點：…；證據：「…原句…」；本題 80 分（有具體數字）。\n   沒有回答的題目只寫：[03:40] AI 面試官問「…」→ 未回答（不計分）。若他沒被問、只是自己插話，要照實寫。");
            sb.AppendLine("二、各維度評分與依據（A~D）。");
            sb.AppendLine("三、待確認事項：履歷與口述不一致、或需要 HR 查證的地方（例如證照、年資、職稱）。這是需要確認，不是加分也不是扣分。");
            sb.AppendLine("四、建議後續關注點：給面試官的追問方向。不寫錄取決定。");
            sb.AppendLine("五、錄影與判讀限制（不影響評分）：只寫畫面、收音、講者辨識上的技術限制；不要描述任何人的表情或動作；沒有就寫「無」。");
            sb.AppendLine("最後（全文最後，只輸出這個區塊，分數為整數）：");
            sb.AppendLine("[[SCORES]]{\"A\":0,\"B\":0,\"C\":0,\"D\":0,\"confidence\":\"高/中/低（講者辨識與題目歸屬的把握程度）\",\"summary\":\"一句話總結\",\"strengths\":\"主要優點\",\"risks\":\"主要待追問處\",\"to_confirm\":\"待確認事項，沒有就寫無\"}[[/SCORES]]");
            if (_liveNotes.TryGetValue(roomCode, out var list))
            {
                List<LiveNote> mine; lock (list) mine = list.Where(x => string.Equals(x.Name, candidate, StringComparison.OrdinalIgnoreCase)).ToList();
                if (mine.Count > 0)
                {
                    sb.AppendLine($"【會議進行中的即時記錄（{candidate}；AI 即時判斷，僅供對照，可能有誤，請以影片為準）】");
                    foreach (var n in mine)
                        sb.AppendLine($"- {n.At:HH:mm}（現實時間，非影片時間）針對「{n.Question}」（問的是：{n.AskedWho}{(n.Addressed ? "" : "，非問他本人")}）｜相關{n.Relevance}/具體{n.Specific}/邏輯{n.Logic}（滿分10）｜{n.Note}｜回答：{n.Answer}");
                }
            }
            return sb.ToString();
        }

        // 語音辨識偶爾會「我是,我是,我是…」重複幾百次：丟給分析前先壓縮，免得塞滿額度、也讓模型跟著重複
        private static string CleanTranscript(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;
            t = System.Text.RegularExpressions.Regex.Replace(t, @"(?<u>[^\s,，。、：:\]\[]{1,8})(?:[,，、 ]*\k<u>){3,}", "${u}…");
            return t;
        }

        private static bool LooksLikeChineseAnalysis(string? a)
        {
            if (string.IsNullOrWhiteSpace(a) || a.Length < 300) return false;
            int cjk = a.Count(c => c >= 0x4e00 && c <= 0x9fff), lat = a.Count(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
            return cjk >= 150 && cjk >= lat;
        }

        private static CandScore? ParseScoreJson(string json, string candidate)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(json, @"\{.*\}", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (!m.Success) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(m.Value);
                var r = doc.RootElement;
                var cs = new CandScore { Name = candidate };
                foreach (var ru in Rubric)
                {
                    if (!r.TryGetProperty(ru.Key, out var e)) return null;
                    int v = 0;
                    if (e.ValueKind == System.Text.Json.JsonValueKind.Number) v = (int)Math.Round(e.GetDouble()); else if (e.ValueKind == System.Text.Json.JsonValueKind.String) int.TryParse(e.GetString(), out v); else return null;
                    cs.S[ru.Key] = Math.Clamp(v, 0, 100);
                }
                string Sx(string k) => r.TryGetProperty(k, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String ? (e.GetString() ?? "") : "";
                cs.Summary = Sx("summary"); cs.Strengths = Sx("strengths"); cs.Risks = Sx("risks"); cs.Confidence = Sx("confidence"); cs.ToConfirm = Sx("to_confirm");
                cs.Total = Math.Round(Rubric.Sum(ru => cs.S[ru.Key] * ru.Weight / 100.0));
                return cs;
            }
            catch { return null; }
        }

        private static string AppendScoreDetail(string body, CandScore cs, string? source)
        {
            var sb = new System.Text.StringBuilder(body.TrimEnd());
            sb.AppendLine().AppendLine().AppendLine("════ 加權評分明細（總分 = 各維度分數 × 權重 加總）════");
            foreach (var ru in Rubric) sb.AppendLine($"{ru.Title}（權重 {ru.Weight}%）：{cs.S[ru.Key]} 分 → 加權 {cs.S[ru.Key] * ru.Weight / 100.0:0.0} 分");
            sb.AppendLine($"AI 綜合評分：{cs.Total:0} 分");
            if (!string.IsNullOrEmpty(source)) sb.AppendLine($"分數來源：{source}");
            sb.AppendLine($"判讀信心：{(string.IsNullOrWhiteSpace(cs.Confidence) ? "未提供" : cs.Confidence)}（信心低時分數僅供參考，請人工對照影片確認）");
            sb.AppendLine("（本分數只依回答內容計算，不含外表、表情、小動作、畫質與收音；AI 僅輔助，不做錄取決定。）");
            return sb.ToString();
        }

        // 解析模型輸出的分數區塊；沒有就回傳 null（由呼叫端用備援補算，不會在報告裡留下「失敗」字樣）
        private static (string Text, CandScore? Score) FinalizeAnalysis(string analysis, string candidate)
        {
            // 結尾標記寫錯（例如 [/SCORES]]）或根本沒寫，也要抓得到、也要從報告裡拿掉
            const string endTag = @"(?:\[\[/SCORES\]\]|\[/SCORES\]\]?|\[\[/SCORES\]|$)";
            var m = System.Text.RegularExpressions.Regex.Match(analysis, @"\[\[SCORES\]\](.*?)" + endTag, System.Text.RegularExpressions.RegexOptions.Singleline);
            var body = System.Text.RegularExpressions.Regex.Replace(analysis, @"\[\[SCORES\]\].*?" + endTag, "", System.Text.RegularExpressions.RegexOptions.Singleline);
            body = System.Text.RegularExpressions.Regex.Replace(body, @"^.*AI\s*綜合評分\s*[：:].*$\r?\n?", "", System.Text.RegularExpressions.RegexOptions.Multiline);
            var cs = m.Success ? ParseScoreJson(m.Groups[1].Value, candidate) : null;
            if (cs == null) return (body.TrimEnd(), null);
            return (AppendScoreDetail(body, cs, null), cs);
        }

        // 備援一：模型沒給分數區塊，就用「已寫好的分析文字」再請它只輸出分數 JSON
        private async Task<CandScore?> ScoreFromTextAsync(string analysis, string candidate)
        {
            try
            {
                var p = "以下是對求職者「" + candidate + "」的面試分析報告。請只根據報告內容，依四個維度各給 0~100 整數分，只輸出一行 JSON，不要其他文字：\n" +
                        "維度：A 答題內容與具體度、B 邏輯結構、C 與職缺及履歷相關度、D 溝通清晰度。報告沒提到、無法判斷的維度給 60 分。\n" +
                        "格式：{\"A\":0,\"B\":0,\"C\":0,\"D\":0,\"confidence\":\"中\",\"summary\":\"一句話\",\"strengths\":\"\",\"risks\":\"\",\"to_confirm\":\"\"}\n\n報告：\n" + Clip(analysis, 6000);
                var raw = await _gemini.AskAsync(p, "你是評分員，只輸出 JSON。", 600);
                return string.IsNullOrWhiteSpace(raw) ? null : ParseScoreJson(raw, candidate);
            }
            catch { return null; }
        }

        // 備援二：用會議中的即時記錄平均來估算
        private CandScore? ScoreFromLiveNotes(string roomCode, string candidate)
        {
            if (!_liveNotes.TryGetValue(roomCode, out var list)) return null;
            List<LiveNote> mine; lock (list) mine = list.Where(x => string.Equals(x.Name, candidate, StringComparison.OrdinalIgnoreCase) && x.Addressed).ToList();
            if (mine.Count == 0) return null;
            int Avg(Func<LiveNote, int> f) => (int)Math.Round(mine.Average(f) * 10);
            var cs = new CandScore { Name = candidate, Confidence = "低", Summary = "依會議中即時記錄估算（共 " + mine.Count + " 次回答）" };
            cs.S["A"] = Avg(x => x.Specific); cs.S["B"] = Avg(x => x.Logic); cs.S["C"] = Avg(x => x.Relevance);
            cs.S["D"] = (int)Math.Round((cs.S["A"] + cs.S["B"] + cs.S["C"]) / 3.0);
            cs.Total = Math.Round(Rubric.Sum(ru => cs.S[ru.Key] * ru.Weight / 100.0));
            return cs;
        }

        private async Task<string> BuildComparisonAsync(Room room, List<CandScore> scores)
        {
            var ranked = scores.OrderByDescending(x => x.Total).ToList();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"面試比較總表｜職缺：{room.Job?.Title ?? "未知職缺"}｜{(room.StartAt ?? room.ScheduledAt ?? DateTime.Now):yyyy-MM-dd}");
            sb.AppendLine();
            sb.AppendLine("【評分依據與權重】");
            foreach (var ru in Rubric) sb.AppendLine($"- {ru.Title}（{ru.Weight}%）：{ru.Basis}");
            sb.AppendLine();
            sb.AppendLine("【排名】");
            int rk = 1;
            foreach (var c in ranked) sb.AppendLine($"{rk++}. {c.Name}　綜合 {c.Total:0} 分（" + string.Join("、", Rubric.Select(ru => $"{ru.Title.Substring(0, 2)}{c.S[ru.Key]}")) + "）");
            sb.AppendLine();
            sb.AppendLine("【各人重點】");
            foreach (var c in ranked) sb.AppendLine($"■ {c.Name}（判讀信心：{(string.IsNullOrWhiteSpace(c.Confidence) ? "未提供" : c.Confidence)}）：{c.Summary}\n  優點：{c.Strengths}\n  待追問：{c.Risks}\n  待確認：{(string.IsNullOrWhiteSpace(c.ToConfirm) ? "無" : c.ToConfirm)}");
            try
            {
                var data = string.Join("\n", ranked.Select(c => $"{c.Name}｜總分{c.Total:0}｜" + string.Join("、", Rubric.Select(ru => $"{ru.Title}{c.S[ru.Key]}")) + $"｜{c.Summary}｜優點:{c.Strengths}｜疑慮:{c.Risks}"));
                var cmp = await _gemini.AskAsync($"以下是同一場面試中各求職者的評分資料（總分已依權重算好）：\n{data}\n\n請用繁體中文寫「比較與後續關注點」：各自的強項、差距主要在哪個維度、各自需要再確認的地方。不要做錄取或不錄取的決定，也不要用「強烈建議」之類的字眼；只能根據上面資料，不可編造；200 字內。", "你是專業人資顧問。", 800);
                if (!string.IsNullOrWhiteSpace(cmp)) { sb.AppendLine(); sb.AppendLine("【比較與後續關注點】"); sb.AppendLine(cmp.Trim()); }
            }
            catch { }
            sb.AppendLine();
            sb.AppendLine("（本表只依回答內容評分，不含外表、表情、畫質與收音；AI 僅輔助，最終由面試官判斷，不做錄取決定。）");
            return sb.ToString();
        }

        // 瀏覽器語音辨識（備援路徑）辨識出一句話：改走 HTTP 讓伺服器記錄並廣播給全場
        [HttpPost]
        public async Task<IActionResult> SpeechTurn([FromForm] string roomCode, [FromForm] string speaker, [FromForm] string? role, [FromForm] string text, [FromForm] bool isQuestion = false)
        {
            if (string.IsNullOrWhiteSpace(roomCode) || string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(text)) return Ok();
            var code = roomCode.Trim();
            AddConvo(code, speaker.Trim(), role ?? "", text);
            QueueLiveNote(code, speaker.Trim(), role ?? "", text);
            MarkResponderAnswered(code, speaker.Trim());
            await DetectAddressedAsync(code, role ?? "", speaker.Trim(), text);
            try { await _hub.Clients.Group(code).SendAsync("SpeechTurnInfo", speaker.Trim(), isQuestion, role ?? "", text.Trim()); } catch { }
            return Ok();
        }


        // ═════════ 回答者鎖定：問誰、誰回答，其他求職者的麥克風暫時關閉 ═════════
        //   觸發：AI 面試官實際說出口的題目有指定對象、或職員/主持人的發言點名了某位求職者（只在能明確判斷是哪一位時才鎖）
        //   解除：該求職者答完後約 8 秒、被點名後 45 秒仍沒回答、換人被點名、或主持人按「解除」
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Name, DateTime At, DateTime? AnsweredAt)> _responder = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, List<string> Names)> _nameCache = new();

        // 最近一次有人出聲（誰）——給前端每 2 秒輪詢「橫幅狀態」用，不再只靠 SignalR 即時廣播（漏收就看不到）
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Who, DateTime At)> _lastSpeaker = new();

        [HttpGet]
        public IActionResult BannerState(string roomCode)
        {
            var code = (roomCode ?? "").Trim();
            _responder.TryGetValue(code, out var r);
            _lastSpeaker.TryGetValue(code, out var s);
            return Json(new
            {
                responder = string.IsNullOrEmpty(r.Name) ? null : r.Name,
                masks = FaceWatchService.CurrentAlert(code),
                speaker = s.Who,
                speakerAgeMs = s.Who == null ? (double?)null : (DateTime.UtcNow - s.At).TotalMilliseconds
            });
        }

        private async Task SetResponderAsync(string code, string? name)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            if (string.IsNullOrWhiteSpace(name))
            {
                if (_responder.TryRemove(code, out _)) { try { await _hub.Clients.Group(code).SendAsync("ResponderChanged", (string?)null); } catch { } }
                return;
            }
            if (_responder.TryGetValue(code, out var cur) && cur.Name == name && (DateTime.UtcNow - cur.At).TotalSeconds < 3) return;
            var stamp = DateTime.UtcNow;
            _responder[code] = (name, stamp, null);
            Console.WriteLine($"[RoomController] 回答者鎖定：房間 {code} 現在請「{name}」回答，其他求職者暫時閉麥。");
            try { await _hub.Clients.Group(code).SendAsync("ResponderChanged", name); } catch { }
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(3000);
                    if (!_responder.TryGetValue(code, out var r) || r.At != stamp) return; // 已換人或已解除
                    if (PersonSpeaking(code)) continue;
                    var now = DateTime.UtcNow;
                    bool done = r.AnsweredAt.HasValue ? (now - r.AnsweredAt.Value).TotalSeconds > 8 : (now - r.At).TotalSeconds > 45;
                    if (done) { await SetResponderAsync(code, null); return; }
                }
            });
        }

        private void MarkResponderAnswered(string code, string speakerLabel)
        {
            if (!_responder.TryGetValue(code, out var r)) return;
            var plain = (speakerLabel ?? "").Replace("求職者-", "").Trim();
            if (string.Equals(plain, r.Name, StringComparison.OrdinalIgnoreCase)) _responder[code] = (r.Name, r.At, DateTime.UtcNow);
        }

        private async Task<List<string>> GetJobseekerNamesAsync(string code)
        {
            if (_nameCache.TryGetValue(code, out var c) && (DateTime.UtcNow - c.At).TotalSeconds < 5) return c.Names;
            var names = new List<string>();
            // 優先用「現在真的在會議室裡」的求職者（Jitsi 顯示名稱）；抓不到才退回報名名單
            try { names = await _botService.GetPresentJobseekerNamesAsync(code); } catch { }
            if (names.Count == 0)
            {
                try
                {
                    var room = await _context.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.JitsiRoomName == code);
                    if (room != null)
                    {
                        var ps = await _context.RoomParticipants.AsNoTracking()
                            .Where(p => p.RoomId == room.Id && p.Role == ParticipantRole.Jobseeker && p.ResumeId != null)
                            .Include(p => p.Resume).ThenInclude(r => r!.Member).ToListAsync();
                        var here = ps.Where(p => p.JoinedAt != null && p.LeftAt == null).ToList();
                        if (here.Count == 0) here = ps;
                        names = here.Select(p => p.Resume?.Member?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct().ToList();
                    }
                }
                catch { }
            }
            _nameCache[code] = (DateTime.UtcNow, names);
            return names;
        }

        // 職員/主持人說的話裡，明確點名了「唯一一位」在場求職者，就鎖定那位為回答者（只針對這一題；答完就放開）
        public static List<string> MatchAddressed(List<string> names, string text)
        {
            bool Has(string n)
            {
                if (n.All(ch => ch < 128)) return Regex.IsMatch(text, @"(?<![A-Za-z0-9])" + Regex.Escape(n) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
                return text.Contains(n, StringComparison.OrdinalIgnoreCase);
            }
            var hit = names.Where(Has).ToList();
            // 語音辨識常把單一英文字母名字（a、b…）聽成同音中文字：只在「句首/標點後」且後面接請求語氣時才當作點名
            if (hit.Count == 0)
            {
                var homo = new Dictionary<string, string> { ["a"] = "欸誒哎啊诶", ["b"] = "比逼必幣畢币毕", ["c"] = "西希", ["d"] = "滴地低迪" };
                foreach (var n in names)
                {
                    if (n.Length != 1 || n[0] >= 128 || !homo.TryGetValue(n.ToLowerInvariant(), out var hs)) continue;
                    if (Regex.IsMatch(text, @"(?:^|[，,。、：:\s])[" + hs + @"](?=\s?(?:請|同學|先生|小姐|做|來|說|講|回答|自我|可以|你|先|麻煩|[，,]))")) hit.Add(n);
                }
            }
            hit = hit.Where(n => !hit.Any(m => m != n && m.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList(); // 「a」被包在「aa」裡不算
            if (hit.Count == 0)
            {
                // 語音辨識常把名字聽錯一個字：姓氏唯一，且姓氏後面緊接 1~2 個中文字再接停頓或請求語氣
                var fz = names.Where(n => n.Length >= 2 && n[0] >= 0x4e00 && n[0] <= 0x9fff
                    && names.Count(m => m.Length > 0 && m[0] == n[0]) == 1
                    && Regex.IsMatch(text, Regex.Escape(n.Substring(0, 1)) + @"[\u4e00-\u9fff]{1,2}(?=[，,、：: 請你可先來說談回答幫跟]|$)")).ToList();
                if (fz.Count == 1) hit = fz;
            }
            return hit;
        }

        private async Task DetectAddressedAsync(string code, string role, string speaker, string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                if (string.Equals(role, "jobseeker", StringComparison.OrdinalIgnoreCase) || (speaker ?? "").StartsWith("求職者") || role == "ai" || (speaker ?? "").StartsWith("AI")) return;
                var names = await GetJobseekerNamesAsync(code);
                if (names.Count < 2) return; // 只有一位求職者時不需要鎖
                var hit = MatchAddressed(names, text);
                Console.WriteLine($"[點名偵測] 房間 {code}：{speaker} 說「{(text.Length > 40 ? text.Substring(0, 40) : text)}」→ 在場求職者=[{string.Join(",", names)}] → 命中=[{string.Join(",", hit)}]");
                if (hit.Count == 1) await SetResponderAsync(code, hit[0]);
            }
            catch { }
        }

        // 🎯 伺服器自己也記「現在有沒有人在講話」：AI 語音合成好、真的要開口前最後再確認一次，有人在講就不送出（不打斷）
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastVoiceUtc = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _pendingUntil = new(); // key: 房間|說話者
        // 🎯 伺服器端「這個房間最後一次有人/AI 出聲」的時間：說話被送到伺服器（LiveTranscribe 那個 HTTP 請求，講完後約 1 秒就到）、
        //    出聲通知、AI 自己講完，都會更新它。冷場 30 秒以它為準——不依賴任何瀏覽器之間的即時通知。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastActivityUtc = new();
        private static void MarkActivity(string code) { if (!string.IsNullOrEmpty(code)) _lastActivityUtc[code.Trim()] = DateTime.UtcNow; }

        internal static bool PersonSpeaking(string code)
        {
            var now = DateTime.UtcNow;
            if (_lastVoiceUtc.TryGetValue(code, out var t) && (now - t).TotalMilliseconds < 1800) return true;
            foreach (var kv in _pendingUntil) if (kv.Key.StartsWith(code + "|") && kv.Value > now) return true;
            return false;
        }

        private static string StripName(string t, IEnumerable<string> names)
        {
            t = (t ?? "").Trim();
            foreach (var n in names) if (t.StartsWith(n, StringComparison.OrdinalIgnoreCase)) { t = t.Substring(n.Length).TrimStart('，', ',', ' ', '、'); break; }
            return t;
        }
        private static double BigramSim(string a, string b)
        {
            HashSet<string> G(string x)
            {
                var h = new HashSet<string>();
                x = new string((x ?? "").Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c) && !char.IsSymbol(c)).ToArray());
                for (int i = 0; i + 1 < x.Length; i++) h.Add(x.Substring(i, 2));
                return h;
            }
            var ga = G(a); var gb = G(b);
            if (ga.Count == 0 || gb.Count == 0) return 0;
            int inter = ga.Count(g => gb.Contains(g));
            return (double)inter / (ga.Count + gb.Count - inter);
        }
        private static bool TooSimilar(string text, List<string> prev, IEnumerable<string> names, double th)
        {
            var t = StripName(text, names);
            foreach (var q in prev) if (BigramSim(t, StripName(q, names)) >= th) return true;
            return false;
        }

        private readonly AppDbContext _context;
        private readonly JitsiBotService _botService;
        private readonly IWebHostEnvironment _env;
        private readonly R2StorageService _storage;
        private readonly GeminiService _gemini;
        private readonly IHubContext<MeetingHub> _hub;

        public RoomController(AppDbContext context, JitsiBotService botService, IWebHostEnvironment env, R2StorageService storage, GeminiService gemini, IHubContext<MeetingHub> hub)
        {
            _context = context;
            _botService = botService;
            _env = env;
            _storage = storage;
            _gemini = gemini;
            _hub = hub;
        }

        // 🎯 主持人是HR替每場面試指定的職員，只看 Rooms.HostEmployeeId，跟帳號角色無關
        private async Task<bool> IsRoomHostAsync(int roomId, int employeeId)
        {
            return await _context.Rooms.AnyAsync(r => r.Id == roomId && r.HostEmployeeId == employeeId);
        }

        private bool IsEmployee()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            return role == "hr" || role == "manager";
        }

        // 🎯 修正：將同步改成非同步，提升高並發時的效能
        public async Task<IActionResult> Index()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var rooms = await _context.Rooms.OrderByDescending(r => r.CreatedTime).ToListAsync();
            return View(rooms);
        }

        [HttpGet]
        public IActionResult Create()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Create(string roomName, DateTime? startAt, DateTime? endAt,
                                                int maxParticipants = 20, string? description = null)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            if (string.IsNullOrWhiteSpace(roomName)) { ModelState.AddModelError("", "房間名稱不能為空"); return View(); }

            // 🎯 防呆機制：防止排程時間前後顛倒
            if (startAt.HasValue && endAt.HasValue && endAt.Value <= startAt.Value)
            {
                ModelState.AddModelError("", "面試結束時間必須晚於開始時間！");
                return View();
            }

            var room = new Room
            {
                RoomName = roomName,
                CreatedTime = DateTime.Now,
                JitsiRoomName = Guid.NewGuid().ToString("N")[..10],
                StartAt = startAt,
                EndAt = endAt,
                IsActive = true
            };

            _context.Rooms.Add(room);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"房間「{roomName}」已建立，代碼：{room.JitsiRoomName}";
            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            var room = await _context.Rooms.FindAsync(id);
            if (room == null) return NotFound();
            return View(room);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(int id, string roomName, DateTime? startAt, DateTime? endAt,
                                             int maxParticipants, bool isActive, string? description)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            if (string.IsNullOrWhiteSpace(roomName)) { ModelState.AddModelError("", "房間名稱不能為空"); return View(); }

            // 🎯 防呆機制：編輯時也要防止時間顛倒
            if (startAt.HasValue && endAt.HasValue && endAt.Value <= startAt.Value)
            {
                ModelState.AddModelError("", "面試結束時間必須晚於開始時間！");
                var currentRoom = await _context.Rooms.FindAsync(id);
                return View(currentRoom);
            }

            var room = await _context.Rooms.FindAsync(id);
            if (room == null) return NotFound();

            room.RoomName = roomName;
            room.StartAt = startAt;
            room.EndAt = endAt;
            room.IsActive = isActive;

            await _context.SaveChangesAsync();
            TempData["Success"] = "房間設定已更新";
            return RedirectToAction("Index");
        }

        [HttpGet]
        public IActionResult EnterCode() => View();

        // 🎯 修正：輸入房間代碼也改為非同步查詢，避免點擊加入時網頁卡死
        [HttpPost]
        public async Task<IActionResult> EnterCode(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode)) { ViewBag.ErrorMessage = "請輸入房間代碼"; return View(); }

            // 🎯 Step C：必須先登入（求職者或員工皆可），才能查代碼
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
            {
                return RedirectToAction("Index", "Login");
            }

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == roomCode.Trim());
            if (room == null) { ViewBag.ErrorMessage = "找不到此房間代碼"; return View(); }

            if (!room.CanEnter())
            {
                ViewBag.Room = room;
                ViewBag.StatusText = room.StatusText();
                return View("RoomNotAvailable");
            }

            // 🎯 Step C：白名單檢查——只有 RoomParticipants 裡受邀的人才能進
            //    （沒有受邀名單的房間，代表是舊有/手動建立的一般房間，維持原本「登入即可進」的行為）
            var hasParticipantList = await _context.RoomParticipants.AnyAsync(p => p.RoomId == room.Id);
            if (hasParticipantList)
            {
                var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
                if (participant == null)
                {
                    ViewBag.ErrorMessage = "您不是這場會議受邀的人員，無法進入";
                    return View();
                }

                // 🎯 求職者一定要先完成適性測驗，才能真的進入會議室（就算已經受邀、知道代碼也一樣）
                //    改成直接導回應徵管理頁、自動彈出測驗彈窗，不管是點「查看」按鈕還是這裡手動輸入代碼進來都一樣
                if (participant.Role == ParticipantRole.Jobseeker)
                {
                    var hasTest = await _context.AptitudeTestResults.AnyAsync(t => t.ResumeId == participant.ResumeId);
                    if (!hasTest)
                    {
                        return Redirect($"/Member/Application?openTest={participant.ResumeId}");
                    }
                }
            }

            return RedirectToAction("Join", new { code = room.JitsiRoomName });
        }

        // 🎯 修正：加入安全性阻擋與非同步優化
        // 🎯 保底輪詢用：不管 SignalR 廣播有沒有送達，前端每隔幾秒問一次「會議現在到底是什麼狀態」，
        //    避免長時間閒置等待時 SignalR 群組悄悄失效，導致畫面永遠卡在等待畫面
        [HttpGet]
        public async Task<IActionResult> GetMeetingStatus(string code)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            if (sessionMemberId == null) return Json(new { meetingStatus = "" });

            var room = await _context.Rooms.AsNoTracking().FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { meetingStatus = "" });

            return Json(new { meetingStatus = room.MeetingStatus });
        }

        public async Task<IActionResult> Join(string code)
        {
            // 安全限制：至少必須是登入的使用者（求職者或員工皆可）才可以進去
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
                return RedirectToAction("Index", "Login");

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Content("房間不存在");

            if (!room.CanEnter())
            {
                ViewBag.Room = room;
                ViewBag.StatusText = room.StatusText();
                return View("RoomNotAvailable");
            }

            // 🎯 Step C：白名單檢查，並記錄受邀者的進場狀態
            var hasParticipantList = await _context.RoomParticipants.AnyAsync(p => p.RoomId == room.Id);
            if (hasParticipantList)
            {
                var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
                if (participant == null)
                {
                    ViewBag.Room = room;
                    ViewBag.ErrorMessage = "您不是這場會議受邀的人員，無法進入";
                    return View("RoomNotAvailable");
                }

                // 🎯 先記住這次 request 進來之前，DB 裡原本的 JoinedAt/LeftAt，
                //    候審室判斷要用「原本」的值，不能用下面（等真的放行才會設定的）新值
                var originalJoinedAt = participant.JoinedAt;
                var originalLeftAt = participant.LeftAt;

                // 🎯 求職者一定要先完成適性測驗，才能真的進入會議室（就算已經受邀、知道代碼也一樣）
                //    改成直接導回應徵管理頁、自動彈出測驗彈窗，而不是顯示一個死路錯誤畫面
                //    （不管是點「查看」按鈕進來、還是直接在網址列打會議代碼手動進來，都會一樣被擋在這裡）
                if (participant.Role == ParticipantRole.Jobseeker)
                {
                    var hasTest = await _context.AptitudeTestResults.AnyAsync(t => t.ResumeId == participant.ResumeId);
                    if (!hasTest)
                    {
                        return Redirect($"/Member/Application?openTest={participant.ResumeId}");
                    }
                }

                // 🎯 候審室機制（原始需求 6）：只要會議「已經開始」，非主持人每一次「重新需要授權」的進場都要候審——
                //    包含這場會議第一次打開連結、以及中途退出（LeftAt 較新）之後想再進來。
                //    但「剛被主持人同意、Lobby 頁面自動重新整理進來」這次不能再擋一次，
                //    不然核准後永遠卡在候審室，變成無限迴圈——用「原本」的 JoinedAt 跟 LeftAt 誰比較新來分辨：
                //      JoinedAt 比較新（或還沒 LeftAt）＝ 目前處於「已核准、正要進場」的狀態，放行
                //      LeftAt 比較新（或從沒被核准過）＝ 需要重新候審
                //    唯一不受影響的情況：會議開始「當下」本來就在等待畫面、跟主持人同步進場的人——
                //    那批人的 Join() 是在 MeetingStatus 還是 NotStarted 的時候就執行過了，不會走到這裡。
                //    🎯 主持人本人一律排除在候審機制之外——他是核准別人的人，不能把自己也卡住（不然沒人能核准他）
                var currentlyAuthorized = originalJoinedAt.HasValue
                    && (!originalLeftAt.HasValue || originalJoinedAt > originalLeftAt);

                var isHostParticipant = room.HostEmployeeId != null && participant.EmployeeId == room.HostEmployeeId;
                if (room.MeetingStatus == "InProgress" && !isHostParticipant && !currentlyAuthorized)
                {
                    // 🐛 修正：這個分支下面的 SaveChangesAsync 只能存 Status=Pending，
                    //    絕對不能連 JoinedAt 都一起存進去——不然候審者的 JoinedAt 會被污染成「有值」，
                    //    下次如果被拒絕（Denied）後又重試，會被誤判成「已經核准過」而直接跳過候審室
                    participant.Status = ParticipantStatus.Pending;
                    await _context.SaveChangesAsync();

                    ViewBag.Room = room;
                    ViewBag.ParticipantId = participant.Id;
                    return View("Lobby");
                }

                // 🎯 注意：這裡只做資格檢查，不寫入資料庫。
                //    Status=Admitted、JoinedAt 要等使用者在 Jitsi 畫面真的按下「加入會議」才算數，
                //    由前端 videoConferenceJoined 事件呼叫 /Room/MarkJoined 來記錄（見下方 MarkJoined action）。
                participant.Status = ParticipantStatus.Admitted;
                participant.JoinedAt = DateTime.Now;

                ViewBag.ParticipantRole = participant.Role;
                ViewBag.IsHost = isHostParticipant;
            }

            ViewBag.Room = room;
            return View();
        }

        // 🎯 候審室機制：卡在候審畫面的人，每隔幾秒問一次「我被同意了嗎」
        [HttpGet]
        public async Task<IActionResult> GetMyAdmissionStatus(string code)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
                return Json(new { status = "" });

            var room = await _context.Rooms.AsNoTracking().FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { status = "" });

            var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
            if (participant == null) return Json(new { status = "" });

            return Json(new { status = participant.Status });
        }

        // 🎯 候審室機制：主持人專用，列出這場會議目前候審中（Pending）的人，主持人畫面靠這個每隔幾秒刷新名單
        [HttpGet]
        public async Task<IActionResult> GetPendingParticipants(string code)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            if (sessionMemberId == null) return Json(new List<object>());

            var room = await _context.Rooms.AsNoTracking().FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new List<object>());

            // 🎯 改版：只有「這場面試的主持人」看得到候審名單（不再看帳號角色）
            if (!await IsRoomHostAsync(room.Id, sessionMemberId.Value)) return Json(new List<object>());

            var pending = await _context.RoomParticipants
                .Where(p => p.RoomId == room.Id && p.Status == ParticipantStatus.Pending)
                .Include(p => p.Resume).ThenInclude(r => r!.Member)
                .Include(p => p.Employee)
                .ToListAsync();

            var result = pending.Select(p => new
            {
                id = p.Id,
                name = p.Role == ParticipantRole.Jobseeker
                    ? (p.Resume?.Member?.Name ?? "求職者")
                    : (p.Employee?.Name ?? "員工"),
                role = p.Role
            });

            return Json(result);
        }

        // 🎯 候審室機制：主持人按下「同意」或「拒絕」
        [HttpPost]
        public async Task<IActionResult> AdmitParticipant(int participantId, bool approve)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            if (sessionMemberId == null)
                return Json(new { success = false, message = "請先登入" });

            var participant = await _context.RoomParticipants.FirstOrDefaultAsync(p => p.Id == participantId);
            if (participant == null) return Json(new { success = false, message = "找不到這位候審中的人" });

            // 🎯 一定要確認這個主持人真的是「這位候審者所在房間」受邀的主持人，不能跨房間操作別人的候審名單
            var isHostOfThisRoom = await IsRoomHostAsync(participant.RoomId, sessionMemberId.Value);
            if (!isHostOfThisRoom)
                return Json(new { success = false, message = "你不是這場面試的主持人" });

            participant.Status = approve ? ParticipantStatus.Admitted : ParticipantStatus.Denied;
            if (approve) participant.JoinedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // 🎯 只有前端在 Jitsi 真的觸發 videoConferenceJoined（使用者按下「加入會議」）時才會呼叫這裡，
        //    這樣 RoomParticipants.JoinedAt 記錄的才是「真的進了視訊會議」的時間，不是「打開這個頁面」的時間。
        [HttpPost]
        public async Task<IActionResult> MarkJoined(string code)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
                return Json(new { success = false });

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { success = false });

            var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
            if (participant == null) return Json(new { success = false });

            participant.Status = ParticipantStatus.Admitted;
            participant.JoinedAt = DateTime.Now;
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // 🎯 對應前端 videoConferenceLeft 事件（不管是自己按掛斷、還是被斷線），記錄離開時間
        [HttpPost]
        public async Task<IActionResult> MarkLeft(string code)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
                return Json(new { success = false });

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { success = false });

            var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
            if (participant == null) return Json(new { success = false });

            participant.LeftAt = DateTime.Now;
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // 🎯 JaaS 錄影完成後會呼叫這個網址（RECORDING_UPLOADED webhook），把下載連結傳過來
        //    要在 JaaS 後台設定 Webhook 網址是「你的公開網址/Room/RecordingWebhook」才收得到
        //    ⚠️ 目前欄位名稱是照 JaaS 文件推測寫的，實際收到的 JSON 格式要等正式串接時再核對調整
        [HttpPost]
        public async Task<IActionResult> RecordingWebhook()
        {
            using var reader = new System.IO.StreamReader(Request.Body);
            var raw = await reader.ReadToEndAsync();

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;

                string? link = null;
                if (root.TryGetProperty("preAuthenticatedLink", out var linkEl))
                {
                    link = linkEl.GetString();
                }
                else if (root.TryGetProperty("data", out var dataEl)
                         && dataEl.TryGetProperty("preAuthenticatedLink", out var linkEl2))
                {
                    link = linkEl2.GetString();
                }

                string? meetingFqn = null;
                if (root.TryGetProperty("meetingFqn", out var fqnEl))
                {
                    meetingFqn = fqnEl.GetString();
                }
                // meetingFqn 格式通常是 "{appId}/{roomCode}"，取最後一段當作 JitsiRoomName 比對
                string? roomCode = meetingFqn?.Split('/').LastOrDefault();

                if (link != null && roomCode != null)
                {
                    var room = await _context.Rooms.FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
                    if (room != null)
                    {
                        room.RecordingUrl = link;
                        await _context.SaveChangesAsync();
                    }
                }
            }
            catch
            {
                // 先不擋，避免 JaaS 收到非 200 一直重送；之後正式串接時再依實際 payload 調整解析邏輯
            }

            return Ok();
        }

        // 🎯 逐字稿改成「每個人各自把自己聽到的內容直接送到伺服器」，不再依賴 SignalR 廣播給其他人再由主持人彙整
        //    （SignalR 對這種長時間會議連線不夠可靠，之前發現只有主持人自己的話會被記錄下來，就是廣播失敗造成的）
        //
        //    🐛 這輪修正：暫存區改成存進共用資料庫的 TranscriptChunks 表，不再用記憶體裡的 static 變數。
        //    原因：這個專案的實際測試方式是每個人在自己電腦上各自跑一份程式，只有 Jitsi 視訊跟資料庫是共用的，
        //    ASP.NET 程序本身不是共用的——存在記憶體裡的暫存區，主持人的「結束會議」只看得到
        //    主持人自己那台電腦記憶體裡的內容，這才是「逐字稿一直只有主持人」的真正原因。
        //    改存資料庫後，不管是誰在哪一台電腦送出的內容，主持人結束會議時都查得到。
        public class TranscriptChunkDto
        {
            public string Sp { get; set; } = "";
            public string Tx { get; set; } = "";
            public string Time { get; set; } = "";
        }

        // 🎯 每個人（不管求職者/職員/主持人）自己講的話，直接送這裡，不透過任何人轉傳
        [HttpPost]
        public async Task<IActionResult> SubmitTranscriptChunk([FromQuery] string roomCode, [FromBody] List<TranscriptChunkDto>? lines)
        {
            // 🐛 這輪新增：這支端點原本完全沒有 log，回報「TranscriptChunks 表整場都沒資料」時，
            //    完全無法判斷是「前端根本沒送」、「送了但這裡收到的是空陣列」，還是「收到了、
            //    但存檔那步失敗」。不管是哪一種情況都先印出來，下次測完直接看伺服器 log 就能定位。
            Console.WriteLine($"[SubmitTranscriptChunk] 收到請求：roomCode={roomCode}，lines={(lines == null ? "null" : lines.Count.ToString())} 筆");

            if (string.IsNullOrEmpty(roomCode) || lines == null || lines.Count == 0)
            {
                Console.WriteLine($"[SubmitTranscriptChunk] roomCode 是空的或 lines 是空的/沒有內容，直接略過不存檔。");
                return Ok(new { success = true });
            }

            var now = DateTime.UtcNow;
            // 🐛 這輪修正（回報：逐字稿裡同一個人一大串話全部都是同一個時間戳記）：
            //    根因就在這裡——原本 ReceivedAt 是整批一次算好一個 now，這批裡的每一行不管
            //    實際上是什麼時候講的，通通蓋上同一個「送到伺服器的當下」時間。
            //    submitMyTranscript() 是整場會議累積到結束才一次用 sendBeacon 全部送出，
            //    等於整場話最後全部變成同一秒的時間戳記，難怪看起來一團亂。
            //    改成優先解析每一行自己帶的 TimeLabel（瀏覽器端當下辨識到那句話時自己算的時間），
            //    還原成真正的 DateTime 當作 ReceivedAt，這樣才是每一行各自對應到實際講話的時間；
            //    解析失敗（格式跑掉之類的意外情況）才退回用整批送達的時間當備援，不會整個掛掉。
            var records = lines
                .Where(l => !string.IsNullOrWhiteSpace(l.Tx))
                .Select(l => new TranscriptChunkRecord
                {
                    RoomCode = roomCode,
                    Speaker = l.Sp,
                    Text = l.Tx,
                    TimeLabel = l.Time,
                    ReceivedAt = TryParseTimeLabel(l.Time, now) ?? now
                })
                .ToList();

            Console.WriteLine($"[SubmitTranscriptChunk] roomCode={roomCode}，過濾掉空白內容後剩 {records.Count} 筆，準備寫入資料庫。");

            if (records.Count > 0)
            {
                _context.TranscriptChunks.AddRange(records);
                await _context.SaveChangesAsync();
                Console.WriteLine($"[SubmitTranscriptChunk] roomCode={roomCode} 已成功寫入 {records.Count} 筆（講者：{string.Join("、", records.Select(r => r.Speaker).Distinct())}）。");
            }
            return Ok(new { success = true });
        }

        // 🎯 解析瀏覽器端送來的中文時間字串（例如「下午02:32:25」或「下午 2:32:25」，
        //    格式可能因為產生的來源不同（.NET ToString 還是瀏覽器 Intl API）而略有差異，
        //    這裡盡量寬鬆比對），還原成當天實際的 DateTime。解析不出來就回傳 null，
        //    呼叫端會自己接住、退回用預設值。
        private static DateTime? TryParseTimeLabel(string? timeLabel, DateTime referenceDate)
        {
            if (string.IsNullOrWhiteSpace(timeLabel)) return null;
            try
            {
                var isPm = timeLabel.Contains("下午");
                var isAm = timeLabel.Contains("上午");
                var digitsPart = System.Text.RegularExpressions.Regex.Match(timeLabel, @"(\d{1,2}):(\d{2}):(\d{2})");
                if (!digitsPart.Success) return null;

                var hour = int.Parse(digitsPart.Groups[1].Value);
                var minute = int.Parse(digitsPart.Groups[2].Value);
                var second = int.Parse(digitsPart.Groups[3].Value);

                if (isPm && hour < 12) hour += 12;
                if (isAm && hour == 12) hour = 0;
                if (hour > 23) return null;

                return new DateTime(referenceDate.Year, referenceDate.Month, referenceDate.Day, hour, minute, second, referenceDate.Kind);
            }
            catch
            {
                return null;
            }
        }

        // 🎯 逐字稿改用這個當主要來源：不再依賴瀏覽器原生 SpeechRecognition
        //    （已證實會被 Jitsi 搶走麥克風獨佔權，大部分人只收到 no-speech，整場話完全沒被記錄下來）。
        //    改成每個人自己把整場錄下來的麥克風音檔，直接送給 Gemini 做語音轉文字，結果併進同一個資料庫暫存表。
        // 🎯 即時轉錄：瀏覽器語音辨識（SpeechRecognition）常常 network 錯誤或回傳空字串，
        //    改由前端每 5 秒送一小段麥克風錄音過來，這裡立刻轉成文字回傳，給 AI 面試官判斷冷場/延伸提問用。
        //    這裡「不存」資料庫，完整逐字稿仍由會議結束時的整場錄音/錄影轉錄產生。
        [HttpPost]
        [RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> LiveTranscribe([FromQuery] string roomCode, IFormFile? audio,
            [FromQuery] string? speaker = null, [FromQuery] long startedAtMs = 0, [FromQuery] double peak = 0, [FromQuery] string? role = null)
        {
            Console.WriteLine($"[LiveTranscribe] 收到音檔：房間 {roomCode}，{(speaker ?? "?")}，{(audio?.Length ?? 0)} bytes，{audio?.ContentType}");
            if (string.IsNullOrEmpty(roomCode) || audio == null || audio.Length < 2000) return Ok(new { text = "" });
            byte[] bytes;
            using (var ms = new MemoryStream()) { await audio.CopyToAsync(ms); bytes = ms.ToArray(); }
            var mime = string.IsNullOrEmpty(audio.ContentType) ? "audio/webm" : audio.ContentType;
            // 這句話剛講完就送到了：馬上記下「剛剛有人講話」（轉文字要幾秒，不能等結果出來才算）；結果若是雜音再還原
            _lastActivityUtc.TryGetValue(roomCode.Trim(), out var prevActivity);
            var actStamp = DateTime.UtcNow; _lastActivityUtc[roomCode.Trim()] = actStamp;
            // 同時處理太多就直接丟掉這段（寧可漏一段，也不要排隊排到幾分鐘後）
            if (System.Threading.Interlocked.Increment(ref _liveInFlight) > 8)
            {
                System.Threading.Interlocked.Decrement(ref _liveInFlight);
                return Ok(new { text = "" });
            }
            string? text;
            try { text = await _gemini.TranscribeLiveAsync(bytes, mime); }
            finally { System.Threading.Interlocked.Decrement(ref _liveInFlight); }
            // 雜音常被 Gemini 瞎猜成韓文/日文：面試是中文（或夾英文），這類結果一律當雜音丟掉
            if (!string.IsNullOrWhiteSpace(text))
            {
                int han = 0, hk = 0, lat = 0;
                foreach (var ch in text)
                {
                    if ((ch >= '\uAC00' && ch <= '\uD7AF') || (ch >= '\u1100' && ch <= '\u11FF') || (ch >= '\u3040' && ch <= '\u30FF')) hk++;
                    else if (ch >= '\u4E00' && ch <= '\u9FFF') han++;
                    else if (char.IsLetter(ch)) lat++;
                }
                if (hk > 0 && hk * 2 >= han + lat + hk) { Console.WriteLine($"[LiveTranscribe] 房間 {roomCode}：{(speaker ?? "?")} 轉出韓文/日文，視為雜音丟掉：{text.Substring(0, Math.Min(20, text.Length))}"); text = ""; }
            }
            if (string.IsNullOrWhiteSpace(text) || text.Contains("無語音內容"))
            {
                if (_lastActivityUtc.TryGetValue(roomCode.Trim(), out var curAct) && curAct == actStamp)
                { if (prevActivity == default) _lastActivityUtc.TryRemove(roomCode.Trim(), out _); else _lastActivityUtc[roomCode.Trim()] = prevActivity; }
                Console.WriteLine($"[LiveTranscribe] 房間 {roomCode}：{(speaker ?? "?")} 這段轉出來是空的（Gemini 回傳：{(text == null ? "null（上面應有 TranscribeLiveAsync 錯誤行）" : "「" + text + "」")}）");
                return Ok(new { text = "", why = _gemini.LastLiveError ?? (string.IsNullOrWhiteSpace(text) ? "Gemini 回傳空白" : "Gemini 判斷沒有人聲") });
            }
            Console.WriteLine($"[LiveTranscribe] 房間 {roomCode}：{(speaker ?? "?")}：{text.Substring(0, Math.Min(40, text.Length))}");
            // 🎯 逐字稿的「誰講的」只認「錄到這句話的那個人自己的瀏覽器」：這裡直接存成一筆逐字稿片段，
            //    並把這句話的最大音量（peak）記在 TimeLabel（"|p=0.123"），之後同一句話被多支麥克風收到時，
            //    以「音量最大的那支麥克風」的主人為準。
            if (!string.IsNullOrWhiteSpace(speaker) && startedAtMs > 0)
            {
                try
                {
                    var at = DateTimeOffset.FromUnixTimeMilliseconds(startedAtMs).ToLocalTime().DateTime;
                    _context.TranscriptChunks.Add(new TranscriptChunkRecord
                    {
                        RoomCode = roomCode.Trim(),
                        Speaker = speaker.Trim(),
                        Text = text.Trim(),
                        TimeLabel = at.ToString("tt h:mm:ss", new System.Globalization.CultureInfo("zh-TW")) + "|p=" + peak.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                        ReceivedAt = at
                    });
                    await _context.SaveChangesAsync();
                }
                catch (Exception ex) { Console.WriteLine($"[LiveTranscribe] 存逐字稿片段失敗：{ex.Message}"); }
            }
            // 🎯 「誰剛講完什麼」改由伺服器直接廣播給全場（原本靠說話者的瀏覽器用 SignalR 發，說話者那端的連線不穩時，
            //    主持人完全收不到，AI 就以為沒人回答、提早又問）
            if (!string.IsNullOrWhiteSpace(speaker))
            {
                var tt = text.Trim();
                AddConvo(roomCode.Trim(), speaker.Trim(), role ?? "", tt);
                QueueLiveNote(roomCode.Trim(), speaker.Trim(), role ?? "", tt);
                MarkResponderAnswered(roomCode.Trim(), speaker.Trim());
                await DetectAddressedAsync(roomCode.Trim(), role ?? "", speaker.Trim(), tt);
                bool isQ = System.Text.RegularExpressions.Regex.IsMatch(tt, @"[?？]\s*$") ||
                           System.Text.RegularExpressions.Regex.IsMatch(tt, @"(嗎|呢\s*$|為什麼|怎麼|如何|能不能|可不可以|有沒有|是不是|多少|哪裡|哪個|什麼時候|可以.*嗎)");
                try { await _hub.Clients.Group(roomCode.Trim()).SendAsync("SpeechTurnInfo", speaker.Trim(), isQ, role ?? "", tt); } catch { }
            }
            return Ok(new { text = text.Trim() });
        }

        [HttpPost]
        [RequestSizeLimit(25_000_000)]
        public async Task<IActionResult> SubmitAudioTranscript([FromQuery] string roomCode, [FromQuery] string speakerName, IFormFile? audio, [FromQuery] long startedAtMs = 0)
        {
            // 🐛 這輪新增：印出伺服器實際收到的 roomCode / speakerName / 音檔大小。
            //    這輪出現「大家的話全部被貼上『主持人』的標籤」這個症狀，需要先確認問題出在
            //    「每個人送出去的 speakerName 參數本身就已經是錯的」還是「名字是對的，但音檔內容
            //    混到別人的聲音」——這行 log 能直接看到伺服器這邊實際收到的 speakerName 是什麼，
            //    拿去跟各自瀏覽器主控台那行 [submitMyAudioTranscript] 印出來的名字互相比對，
            //    兩邊對不起來就是參數傳遞的問題，兩邊一致但內容是別人的聲音就是漏音/麥克風的問題。
            Console.WriteLine($"[SubmitAudioTranscript] 收到請求：roomCode={roomCode}，speakerName={speakerName}，音檔大小={(audio?.Length ?? 0)} bytes");

            if (string.IsNullOrEmpty(roomCode) || audio == null || audio.Length == 0)
                return Ok(new { success = true });

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await audio.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            var mimeType = string.IsNullOrEmpty(audio.ContentType) ? "audio/webm" : audio.ContentType;

            // 🎯 優先用「帶時間」的轉錄：每句話各存一筆，ReceivedAt = 音檔開始錄的時間 + 這句話的起點秒數，
            //    逐字稿和影片字幕才對得上真正講話的時間。失敗再退回下面舊的整段轉錄。
            if (startedAtMs > 0)
            {
                var timed = await _gemini.TranscribeAudioTimedAsync(bytes, mimeType);
                if (timed != null)
                {
                    if (timed.Count == 0)
                    {
                        Console.WriteLine($"[SubmitAudioTranscript] speakerName={speakerName} 帶時間轉錄判定無有效語音，不存入。");
                        return Ok(new { success = true });
                    }
                    var start = DateTimeOffset.FromUnixTimeMilliseconds(startedAtMs).ToLocalTime().DateTime;
                    var zh = new System.Globalization.CultureInfo("zh-TW");
                    foreach (var (offsetSec, lineText) in timed)
                    {
                        var at = start.AddSeconds(offsetSec);
                        _context.TranscriptChunks.Add(new TranscriptChunkRecord
                        {
                            RoomCode = roomCode,
                            Speaker = speakerName,
                            Text = lineText,
                            TimeLabel = at.ToString("tt h:mm:ss", zh),
                            ReceivedAt = at
                        });
                    }
                    await _context.SaveChangesAsync();
                    Console.WriteLine($"[SubmitAudioTranscript] speakerName={speakerName} 帶時間轉錄成功，共 {timed.Count} 句。");
                    return Ok(new { success = true });
                }
                Console.WriteLine($"[SubmitAudioTranscript] speakerName={speakerName} 帶時間轉錄失敗，退回整段轉錄。");
            }

            var text = await _gemini.TranscribeAudioAsync(bytes, mimeType);

            if (string.IsNullOrWhiteSpace(text) || text.Contains("無語音內容"))
            {
                Console.WriteLine($"[SubmitAudioTranscript] speakerName={speakerName} 這段轉錄結果是空的或判定無語音內容，不存入。");
                return Ok(new { success = true }); // 轉錄失敗或整段都沒講話，不用存
            }

            Console.WriteLine($"[SubmitAudioTranscript] speakerName={speakerName} 轉錄成功，內容前 40 字：{text.Substring(0, Math.Min(40, text.Length))}");

            // 🐛 這輪修正（回報：逐字稿裡出現一段時間完全兜不起來的內容，例如整場應該是凌晨，
            //    卻冒出一行「下午 4:11」）：根因是這裡 TimeLabel 用 DateTime.Now（本地時間）格式化，
            //    但 ReceivedAt 卻存 DateTime.UtcNow（UTC 時間）——而 SaveTranscript 合併/輸出逐字稿時，
            //    是直接把 ReceivedAt 當「本地時鐘時間」拿去格式化顯示（不是用 TimeLabel 那個字串），
            //    等於把 UTC 時間當本地時間顯示，台灣時區（UTC+8）差了整整 8 小時，難怪對不起來。
            //    改成 TimeLabel 跟 ReceivedAt 都統一用 DateTime.Now（本地時間），
            //    跟 SubmitTranscriptChunk／MeetingHub.StartAt 那些地方的時間基準一致。
            var timeLabel = DateTime.Now.ToString("tt h:mm:ss", new System.Globalization.CultureInfo("zh-TW"));
            _context.TranscriptChunks.Add(new TranscriptChunkRecord
            {
                RoomCode = roomCode,
                Speaker = speakerName,
                Text = text,
                TimeLabel = timeLabel,
                ReceivedAt = DateTime.Now
            });
            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        // 🐛 這輪新增：讓 AI 面試官虛擬人（Simli）真的開口說話用的觸發端點。
        //    原本「冷場提問」偵測到要說話時，是在觸發的那個人自己的瀏覽器裡用 SpeechSynthesis 講給自己聽，
        //    不會真的傳到會議裡讓其他人聽到。現在改成呼叫這裡，轉發給 JitsiBotService 去驅動 AI 面試官
        //    這個「真正的 Jitsi 參與者」把話說出來（Gemini TTS 生語音 + Simli 對嘴型）。
        //    這裡不驗證是誰呼叫的（求職者/職員都可能觸發冷場提問），只要帶對房間代碼就會嘗試觸發；
        //    如果那個房間根本沒有 AI 面試官在線上（例如還沒設定 Simli），就安靜地不做事。
        //
        //    🐛 這輪再新增：AI 開始說話、講完了，都會透過 SignalR 廣播「AiSpeakingStateChanged」
        //    給這個房間所有人（不只觸發的那個人），前端收到後會：
        //      1. 全場一起暫停冷場計時（不然還沒收到廣播的人可能在 AI 講話當下又觸發一次，疊在一起搶話）
        //      2. 全場的字幕同時顯示 AI 現在講的話（不再只有觸發的人自己看得到）
        //      3. AI 講完後，全場的冷場計時器從這個時間點重新起算，而不是各自猜什麼時候該恢復
        //    如果這個房間 AI 已經在說話中（IsSpeaking 回傳 true），直接略過這次觸發，不重疊。
        [HttpPost]
        public async Task<IActionResult> AiSpeak([FromForm] string roomCode, [FromForm] string text)
        {
            if (string.IsNullOrEmpty(roomCode) || string.IsNullOrWhiteSpace(text))
            {
                // 🐛 這輪新增：連「參數是空的」這種最早期就會被擋下來的情況也印出來，
                //    不然完全查不到 AiSpeak 這個端點到底有沒有被呼叫過
                Console.WriteLine($"[RoomController] AiSpeak：收到請求但 roomCode 或 text 是空的，roomCode={roomCode}，text={text}");
                return Ok(new { success = true });
            }

            roomCode = roomCode.Trim();

            // 🎯 真正開口前的最後一道伺服器端把關（預先準備好的題目是直接走這裡的，不會經過 AiNextQuestion 的檢查）：
            //    距離上次有人出聲／AI 講完不到 17 秒、或有人正在講話，一律不准開口，回覆「再等幾秒」。
            {
                double? aud0 = await _botService.GetAudibleSilenceSecondsAsync(roomCode);
                double? q0 = null;
                if (_lastActivityUtc.TryGetValue(roomCode, out var act0)) q0 = (DateTime.UtcNow - act0).TotalSeconds;
                // 機器人混音聽到的聲音只在「真的剛剛才有聲音（3 秒內）」才算，避免背景雜音讓 AI 永遠不能開口
                if (aud0.HasValue && aud0.Value < 3 && (!q0.HasValue || aud0.Value < q0.Value)) q0 = aud0;
                if (PersonSpeaking(roomCode)) q0 = 0;
                if (q0.HasValue && q0.Value < 17)
                {
                    var w0 = Math.Max(1, (int)Math.Ceiling(20 - q0.Value));
                    Console.WriteLine($"[RoomController] AiSpeak：房間 {roomCode} 距離上次有人出聲只有 {q0.Value:0.0} 秒，不准開口（活動計時={(q0 ?? -1):0.0}，混音靜音={(aud0 ?? -1):0.0}），請前端再等 {w0} 秒。[伺服器版本 v0300]");
                    return Ok(new { success = true, refused = true, waitSec = w0 });
                }
            }

            if (!_botService.IsSpeaking(roomCode) && _aiPending.TryGetValue(roomCode, out var pend) && pend.Text == text.Trim())
            {
                // 這題真的要說出口了：記進整場「已問過」清單、輪流計數、上一題是問誰
                _aiPending.TryRemove(roomCode, out _);
                var qs = _aiQuestions.GetOrAdd(roomCode, _ => new List<string>());
                lock (qs) qs.Add(pend.Text);
                if (pend.Name != null && !pend.ModeU) _aiAskCount.AddOrUpdate($"{roomCode}|{pend.Name}", 1, (_, v) => v + 1);
                _aiLast[roomCode] = (pend.Name, pend.Text);
                if (pend.Name != null && IntroBroadRx.IsMatch(pend.Text)) _introAskCount.AddOrUpdate($"{roomCode}|{pend.Name}", 1, (_, v) => v + 1);
            }

            if (_botService.IsSpeaking(roomCode))
            {
                Console.WriteLine($"[RoomController] AiSpeak：房間 {roomCode} 的 AI 面試官已經在說話中，這次的觸發略過（避免搶話）：「{text}」");
                return Ok(new { success = true, skipped = true });
            }

            // 🐛 這輪新增：這裡一定要印出來，不然沒辦法從伺服器 log 確認 /Room/AiSpeak 這個端點
            //    到底有沒有真的被觸發過——之前只有「略過」的分支會印，正常觸發完全沒有留下任何痕跡。
            Console.WriteLine($"[RoomController] AiSpeak：房間 {roomCode} 收到觸發請求，內容：「{text}」，準備轉發給 JitsiBotService。");

            _ = SpeakAndBroadcastAsync(roomCode, text); // AI 的話要等「聲音真的開始播」才記進對話紀錄（見 SpeakAndBroadcastAsync） // 🎯 不擋住呼叫端：語音合成+送進 Simli 需要幾秒鐘，讓它背景執行就好
            return Ok(new { success = true });
        }

        // 🎯 「有人正在講話／講完了」改走 HTTP 由伺服器廣播（比說話者瀏覽器自己的 SignalR 連線可靠）
        //    pending: 1=正在講話或轉文字中, 0=這句話處理完了, -1=只是出聲通知
        [HttpPost]
        public async Task<IActionResult> VoicePing([FromForm] string roomCode, [FromForm] string? speaker, [FromForm] int pending = -1, [FromForm] int hold = 5, [FromForm] int empty = 0, [FromForm] string? role = null)
        {
            if (string.IsNullOrWhiteSpace(roomCode)) return Ok();
            roomCode = roomCode.Trim();
            var id = string.IsNullOrWhiteSpace(speaker) ? "?" : speaker.Trim();
            try
            {
                if (pending == -1 || (pending == 1 && hold <= 5)) { _lastVoiceUtc[roomCode] = DateTime.UtcNow; MarkActivity(roomCode); if (id != "?") _lastSpeaker[roomCode] = (id, DateTime.UtcNow); }
                if (pending == 1) _pendingUntil[roomCode + "|" + id] = DateTime.UtcNow.AddSeconds(Math.Min(Math.Max(hold, 1), 30));
                else if (pending == 0) _pendingUntil.TryRemove(roomCode + "|" + id, out _);
                if (empty == 1) _lastVoiceUtc.TryRemove(roomCode, out _); // 雜音：不要讓它擋住 AI
                if (pending == -1 || (pending == 1 && hold <= 5)) await _hub.Clients.Group(roomCode).SendAsync("SpeechDetected", id, role ?? ""); // 轉文字中(hold>5)不算「又有聲音」
                if (pending >= 0) await _hub.Clients.Group(roomCode).SendAsync("UtterancePendingChanged", id, pending == 1, hold);
                if (empty == 1) await _hub.Clients.Group(roomCode).SendAsync("VoiceFalseAlarm", id); // 這段聲音轉不出任何話（雜音），冷場計時不該被它歸零
            }
            catch { }
            return Ok();
        }

        // 🎯 有人開口：馬上讓 AI 面試官停止說話（不等語音辨識，不管對方說了什麼）
        [HttpPost]
        public async Task<IActionResult> AiStop([FromForm] string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode)) return Ok(new { success = true });
            var stopped = await _botService.StopSpeakingAsync(roomCode.Trim());
            return Ok(new { success = true, stopped });
        }

        // 🎯 AI 面試官「該不該說話、說什麼」的決定點。
        //    依最近的對話（職員、求職者說了什麼）+ 職缺條件 + 在場求職者各自的履歷，產生「一句」延伸提問；
        //    可以指定其中一位求職者（句子以姓名開頭），不同求職者問不同的問題。
        //    沒有值得問的內容時回傳 text = null，前端就不讓 AI 開口（不說鼓勵、客套這類廢話）。
        //    開場自我介紹不走這裡（寫在 JitsiBotService）。
        [HttpPost]
        public async Task<IActionResult> AiNextQuestion([FromBody] AiNextQuestionRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.RoomCode)) return Ok(new { text = (string?)null });
            var code = req.RoomCode.Trim();
            Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 收到冷場提問請求（對話 {(req.Lines?.Count ?? 0)} 句）。[伺服器版本 v0300]");
            if (_botService.IsSpeaking(code)) return Ok(new { text = (string?)null });

            // 🎯 最後的保險（不管前端計時有沒有算錯）：會議的混音裡不到 17 秒前還聽得到人聲，就還不能因為冷場而開口。
            //    這個量測在伺服器端的 AI 面試官自己身上，直接聽會議裡實際送出的聲音。
            if (!req.Prefetch)
            {
                double? quiet = await _botService.GetAudibleSilenceSecondsAsync(code);
                string quietSrc = "會議混音";
                if (_lastActivityUtc.TryGetValue(code, out var actAt))
                {
                    var srvQuiet = (DateTime.UtcNow - actAt).TotalSeconds;
                    if (!quiet.HasValue || srvQuiet < quiet.Value) { quiet = srvQuiet; quietSrc = "伺服器收到的說話/出聲記錄"; }
                }
                if (PersonSpeaking(code)) { quiet = 0; quietSrc = "有人正在講話或話還在轉文字"; }
                if (quiet.HasValue && quiet.Value < 18)
                {
                    var wait = Math.Max(1, (int)Math.Ceiling(20 - quiet.Value));
                    Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 依「{quietSrc}」，距離上次有人出聲只有 {quiet.Value:0.0} 秒，還不能開口，請前端再等 {wait} 秒。");
                    return Ok(new { text = (string?)null, waitSec = wait });
                }
            }

            var room = await _context.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.JitsiRoomName == code);
            if (room == null) return Ok(new { text = (string?)null });

            Job? job = null;
            if (room.JobsId.HasValue)
                job = await _context.Jobs.AsNoTracking().Include(j => j.SkillTags).FirstOrDefaultAsync(j => j.Id == room.JobsId.Value);

            var participants = await _context.RoomParticipants.AsNoTracking()
                .Where(p => p.RoomId == room.Id && p.Role == ParticipantRole.Jobseeker && p.ResumeId != null)
                .Include(p => p.Resume).ThenInclude(r => r!.Member)
                .Include(p => p.Resume).ThenInclude(r => r!.Educations)
                .Include(p => p.Resume).ThenInclude(r => r!.WorkExperiences)
                .Include(p => p.Resume).ThenInclude(r => r!.Portfolios)
                .ToListAsync();

            // 優先只看「已進場、還沒離開」的求職者；都判斷不出來時退回全部受邀的求職者
            var present = participants.Where(p => p.JoinedAt != null && p.LeftAt == null).ToList();
            if (present.Count == 0) present = participants;

            string Clip(string? s, int n) { s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length <= n ? s : s.Substring(0, n) + "…"; }

            var sbResume = new StringBuilder();
            foreach (var p in present)
            {
                var r = p.Resume; if (r == null) continue;
                sbResume.AppendLine($"【{r.Member?.Name}】");
                var edu = string.Join("；", r.Educations.OrderBy(e => e.SortOrder).Take(3).Select(e => $"{e.SchoolName} {e.Major}（{e.EduLevel}）"));
                if (edu.Length > 0) sbResume.AppendLine("學歷：" + Clip(edu, 150));
                var works = string.Join("；", r.WorkExperiences.OrderBy(w => w.SortOrder).Take(4).Select(w => $"{w.CompanyName} {w.JobTitle}：{Clip(w.JobDescription, 80)}"));
                if (works.Length > 0) sbResume.AppendLine("經歷：" + Clip(works, 400));
                var ports = string.Join("；", r.Portfolios.OrderBy(x => x.SortOrder).Take(3).Select(x => $"{x.Title}：{Clip(x.Description, 60)}"));
                if (ports.Length > 0) sbResume.AppendLine("作品：" + Clip(ports, 250));
                if (!string.IsNullOrWhiteSpace(r.Autobiography)) sbResume.AppendLine("自傳摘要：" + Clip(r.Autobiography, 250));
                sbResume.AppendLine();
            }

            var sbJob = new StringBuilder();
            if (job != null)
            {
                sbJob.AppendLine($"職稱：{job.Title}（{job.Department}）");
                if (!string.IsNullOrWhiteSpace(job.Description)) sbJob.AppendLine("職缺說明：" + Clip(job.Description, 300));
                if (!string.IsNullOrWhiteSpace(job.ExperienceRequired)) sbJob.AppendLine("經驗要求：" + Clip(job.ExperienceRequired, 100));
                if (!string.IsNullOrWhiteSpace(job.OtherRequirements)) sbJob.AppendLine("其他要求：" + Clip(job.OtherRequirements, 200));
                var tags = string.Join("、", job.SkillTags.Select(s => s.Tag).Take(12));
                if (tags.Length > 0) sbJob.AppendLine("技能：" + tags);
            }

            var lines = (req.Lines ?? new List<AiConvoLine>()).TakeLast(20).ToList();
            // 🎯 以伺服器自己記的對話為準（主持人瀏覽器漏收廣播時，req.Lines 會停在舊內容，AI 就一直問舊話題）
            if (_convo.TryGetValue(code, out var srvConvo))
            {
                List<AiConvoLine> snap; lock (srvConvo) snap = srvConvo.TakeLast(20).ToList();
                if (snap.Count(x => x.Role != "ai") > 0 && (snap.Count(x => x.Role != "ai") >= lines.Count(x => x.Role != "ai")))
                {
                    if (snap.Count != lines.Count) Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 改用伺服器記錄的最新對話（{snap.Count} 句；瀏覽器回報 {lines.Count} 句）。");
                    lines = snap;
                }
            }
            var sbConvo = new StringBuilder();
            foreach (var l in lines) sbConvo.AppendLine($"{l.Name}：{Clip(l.Text, 200)}");

            // 對話內容太少（例如還沒人實質講話）就不要硬問
            if (lines.Count(l => l.Role != "ai") == 0)
            {
                // 沒有任何文字、但前端判定職員/主持人已經開口且冷場超過 30 秒：不能因為語音辨識沒出字就一直沉默，
                // 改依職缺條件與求職者履歷提一個專業問題
                Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 沒有任何辨識出來的文字，改依職缺與履歷提問。");
                lines.Add(new AiConvoLine { Name = "職員", Role = "manager", Text = "（職員剛剛有發言，語音沒辨識出文字）依職缺與履歷提一個專業問題" });
                sbConvo.Clear(); foreach (var l in lines) sbConvo.AppendLine($"{l.Name}：{Clip(l.Text, 200)}");
            }
            if (false)
            {
                Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 收到請求，但對話裡沒有任何人類發言（語音辨識沒有產生文字），所以不發言。目前收到 {lines.Count} 句，全是 AI。");
                return Ok(new { text = (string?)null });
            }

            // 🎯 「要延伸的話題」＝最近一句非 AI 的發言。AI 的問題必須從這句延伸，不能另起爐灶跳到履歷別的地方。
            // 「謝謝」「嗯」「對」這種太短的客套話不當作要延伸的話題（否則 AI 會把職員剛問的題目忘掉）
            var lastHuman = lines.LastOrDefault(l => l.Role != "ai" && ((l.Text ?? "").Trim().Length >= 6))
                            ?? lines.LastOrDefault(l => l.Role != "ai");
            var anchorText = lastHuman != null ? $"{lastHuman.Name}：{Clip(lastHuman.Text, 200)}" : "（無）";

            // 🎯 「目前面試進行到哪個主題」＝職員/主持人最近一次的提問或要求；更早的職員提問＝已經談完的階段
            bool IsStaffLine(AiConvoLine l) => l.Role != "ai" && !string.Equals(l.Role, "jobseeker", StringComparison.OrdinalIgnoreCase) && !(l.Name ?? "").StartsWith("求職者");
            var staffLines = lines.Where(IsStaffLine).ToList();
            var themeLine = staffLines.LastOrDefault(l => (l.Text ?? "").Trim().Length >= 6) ?? staffLines.LastOrDefault();
            var themeText = themeLine != null ? $"{themeLine.Name}：{Clip(themeLine.Text, 120)}" : "（職員還沒有提問）";
            var earlierStaff = staffLines.Count > 1 ? string.Join("\n", staffLines.Take(staffLines.Count - 1).Select(l => "- " + Clip(l.Text, 80))) : "（無）";
            // 職員已經問過的題目也要避免被 AI 換句話說重問
            var staffQTexts = staffLines.Select(l => l.Text ?? "").Where(t => t.Length > 4).ToList();

            // 🎯 判斷現在是哪一種情境，直接告訴模型該怎麼做（不讓它自己亂選）
            string scenario;
            var lastIsJobseeker = lastHuman != null &&
                (string.Equals(lastHuman.Role, "jobseeker", StringComparison.OrdinalIgnoreCase)
                 || (lastHuman.Name ?? "").StartsWith("求職者"));
            var lastLooksLikeAsk = req.LastWasQuestion || (lastHuman != null &&
                System.Text.RegularExpressions.Regex.IsMatch(lastHuman.Text ?? "", @"[？?]|請|嗎|呢|什麼|如何|怎麼|為什麼|能否|可以"));
            if (lastHuman == null) scenario = "（無）";
            else if (!lastIsJobseeker && lastLooksLikeAsk)
                scenario = "情境A：職員/主持人剛提出問題或要求，求職者還沒回答（或沒回答完）。請「就同一個問題」延伸引導：換個更具體的說法、縮小範圍、或指出可以從哪個面向開始談（可結合該求職者履歷中的具體經歷），幫求職者接得上；不可換成別的題目。";
            else if (lastIsJobseeker)
                scenario = "情境B：求職者剛回答完，職員/主持人還沒接著問。請針對他剛回答裡的具體內容追問一個細節（例如做法、成果、遇到的困難、數字），或視情況自然銜接履歷/職缺要求的下一題。";
            else
                scenario = "情境C：職員/主持人剛說了一段話（不是問題）。請順著他的話題，向適合的求職者提出一個延伸問題。";
            // 職員/主持人只是在收尾、道謝、表示「我們清楚了」（不是提問）：不能當成「有人沒回答」，也不要追問剛結束的話題，換一個新方向
            bool closingStmt = lastHuman != null && !lastIsJobseeker && !lastLooksLikeAsk
                && System.Text.RegularExpressions.Regex.IsMatch(lastHuman.Text ?? "", @"謝謝|感謝|清楚了|了解了|瞭解了|明白了|沒問題|先這樣|好的|好，|好,|OK|ok");
            if (closingStmt)
                scenario = "情境D：職員/主持人剛才只是在收尾、道謝或表示「這個話題我們清楚了」（不是提問），並沒有人漏回答。請不要追問剛結束的那個話題，也不要把它當成有人沒回答；請換一個新的方向：依該求職者履歷裡的具體經歷出一個情境題，或問這個職缺上班時可能遇到的狀況。";

            if (lastHuman != null && !lastIsJobseeker && System.Text.RegularExpressions.Regex.IsMatch(lastHuman.Text ?? "", @"自我介紹|介紹一下自己|做.{0,3}介紹|先介紹"))
                scenario += "\n【特別注意】職員剛剛要求的是「自我介紹」，到現在還沒有人做。你只要請指定的那一位求職者開始簡單自我介紹就好（例如「○○，那就由你先簡單做個自我介紹吧」），不要問履歷裡的專案或技術細節。";
            var names = string.Join("、", present.Select(p => p.Resume?.Member?.Name).Where(n => !string.IsNullOrEmpty(n)));

            // 🎯 決定「這句話只能對誰說」：最近一句人類發言點名了某位求職者，或剛回答完的就是那位，就只能對他說，
            //    不能自己挑別人（例如主持人說「從 b 開始」，AI 卻去問吳鼠鼠）。
            var presentNames = present.Select(p => p.Resume?.Member?.Name).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Distinct().ToList();
            string? targetName = null;
            string turnText = "";
            bool JsLine0(AiConvoLine l) => l.Role != "ai" && (string.Equals(l.Role, "jobseeker", StringComparison.OrdinalIgnoreCase) || (l.Name ?? "").StartsWith("求職者"));
            if (lastHuman != null)
            {
                var htxt = lastHuman.Text ?? "";
                // 職員一段話常被切成好幾句：把「最後一句 AI／求職者發言之後」連續的職員/主持人發言接起來一起看，名字可能在前面那句
                if (!lastIsJobseeker)
                {
                    var turnParts = new List<string>();
                    for (int ti = lines.Count - 1; ti >= 0 && turnParts.Count < 4; ti--)
                    {
                        var tl = lines[ti];
                        if (tl.Role == "ai" || JsLine0(tl)) break;
                        turnParts.Insert(0, tl.Text ?? "");
                    }
                    if (turnParts.Count > 0) htxt = string.Join("，", turnParts);
                }
                turnText = htxt;
                var mentioned = presentNames.Where(n => htxt.Contains(n, StringComparison.OrdinalIgnoreCase)).ToList();
                if (mentioned.Count == 0)
                {
                    // 語音辨識常把名字聽錯（例如「吳鼠鼠」變成「吳叔叔」「吳淑淑」）：姓氏在前 10 個字內、後面接 1~2 個字再接標點，
                    // 而且在場的人裡只有一位是這個姓，就當作點名那位
                    var head = htxt;
                    var fuzzy = presentNames.Where(n => n.Length >= 2 && char.GetUnicodeCategory(n[0]) == System.Globalization.UnicodeCategory.OtherLetter
                        && presentNames.Count(m => m.Length > 0 && m[0] == n[0]) == 1
                        && System.Text.RegularExpressions.Regex.IsMatch(head, System.Text.RegularExpressions.Regex.Escape(n.Substring(0, 1)) + @"[\u4e00-\u9fff]{1,2}(?=[，,、：: 請你可先來說談回答幫跟]|$)")).ToList();
                    if (fuzzy.Count == 1) mentioned = fuzzy;
                }
                // 名字被聽成完全不像的字（例如「吳鼠鼠」→「叔叔」，沒有姓氏可比對）：請模型依同音/相近音，從在場名單挑出最可能被點名的人
                if (mentioned.Count == 0 && presentNames.Count > 1 && !lastIsJobseeker && htxt.Trim().Length > 0)
                {
                    try
                    {
                        var recentCtx = string.Join("\n", lines.Where(l => l.Role != "ai").TakeLast(4).Select(l => $"{l.Name}：{Clip(l.Text, 60)}"));
                        var rp = $"面試中職員/主持人說了下面這些話（語音辨識，常把名字聽成同音或相近的字，例如「吳鼠鼠」可能被聽成「吳叔叔」「吳淑淑」「叔叔」「書書」）：\n{recentCtx}\n重點是最後一句：「{Clip(htxt, 60)}」\n在場求職者名單：{string.Join("、", presentNames)}\n這句話有點名其中某一位求職者嗎（例如開頭稱呼對方、叫某人做某事）？有的話只回覆名單中那位的完整姓名（依發音最接近的判斷），沒有點名任何人就只回覆「無」。不要解釋。";
                        var ans = (await _gemini.AskAsync(rp, "你只輸出一個名字或「無」。", 30))?.Trim() ?? "";
                        var hit = presentNames.FirstOrDefault(n => ans.Contains(n, StringComparison.OrdinalIgnoreCase));
                        if (hit != null) { mentioned = new List<string> { hit }; Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 依發音判斷被點名的是「{hit}」（原句：{Clip(htxt, 30)}）"); }
                    }
                    catch { }
                }
                if (mentioned.Count == 1) targetName = mentioned[0];
            }
            // 🎯 自我介紹階段：如果會議一開始是在請大家自我介紹（職員/主持人或 AI 提出），在場求職者還沒全部講完之前，
            //    冷場時就請「還沒介紹的那一位」接著自我介紹；全部都介紹完了，才進入依履歷出題。
            //    如果第一個題目不是自我介紹，這段完全不適用。
            bool JsLine(AiConvoLine l) => l.Role != "ai" && (string.Equals(l.Role, "jobseeker", StringComparison.OrdinalIgnoreCase) || (l.Name ?? "").StartsWith("求職者"));
            var introRx = new System.Text.RegularExpressions.Regex(@"自我介紹|介紹一下自己|簡單做個介紹|做.{0,3}介紹|先介紹");
            bool introPhase = false;
            string newcomerNote = "";
            {
                int introIdx = lines.FindIndex(l => !JsLine(l) && introRx.IsMatch(l.Text ?? "")); // 第一次要求自我介紹的位置（之後職員又提到「自我介紹」不能把已介紹的人洗掉）
                if (introIdx >= 0 && (lastIsJobseeker || lastHuman == null || introRx.IsMatch(turnText)))
                {
                    var introduced = lines.Skip(introIdx + 1).Where(l => JsLine(l) && (l.Text ?? "").Trim().Length >= 8)
                        .Select(l => (l.Name ?? "").Replace("求職者-", "").Trim()).ToList();
                    // 伺服器完整對話紀錄裡，講過累計 25 字以上的求職者也算已經介紹過（避免只看最近幾句而漏掉）
                    if (_convo.TryGetValue(code, out var fullC))
                    {
                        List<AiConvoLine> snapC; lock (fullC) snapC = fullC.ToList();
                        introduced.AddRange(snapC.Where(JsLine).GroupBy(l => (l.Name ?? "").Replace("求職者-", "").Trim())
                            .Where(g => g.Sum(x => (x.Text ?? "").Trim().Length) >= 25).Select(g => g.Key));
                    }
                    var notYet = presentNames.Where(n => !introduced.Any(i => string.Equals(i, n, StringComparison.OrdinalIgnoreCase))
                        && lines.Count(l => l.Role == "ai" && (l.Text ?? "").StartsWith(n) && IntroBroadRx.IsMatch(l.Text ?? "")) < 2
                        && (_introAskCount.TryGetValue($"{code}|{n}", out var ic) ? ic : 0) < 2).ToList();
                    if (notYet.Count > 0)
                    {
                        introPhase = true;
                        if (targetName == null) targetName = notYet[0];
                        Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 還在自我介紹階段，尚未介紹：{string.Join("、", notYet)}，這次請「{targetName}」自我介紹。");
                    }
                }
            }
            // 🎯 職員/主持人剛提問、求職者還沒回答，但聽不出問的是誰：絕不能自己挑人（不用「上一位」也不輪流），
            //    只有一位求職者時就是他；多位時用不點名的方式讓大家接話。
            bool ambiguousStaffQ = false;
            if (targetName == null && !introPhase && scenario.StartsWith("情境A") && !req.AiUnanswered && presentNames.Count > 0)
            {
                if (presentNames.Count == 1) targetName = presentNames[0];
                else { ambiguousStaffQ = true; Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 職員剛提問但聽不出問的是誰，這次不點名。"); }
            }
            // 沒有人被點名：輪流。剛回答完的人可以被追問，但如果他已經比最少被問的人多被問 2 次以上，就換最少被問的人。
            if (targetName == null && !ambiguousStaffQ && presentNames.Count > 0)
            {
                int Cnt(string n) => _aiAskCount.TryGetValue($"{code}|{n}", out var v) ? v : 0;
                var least = presentNames.Min(Cnt);
                var leastNames = presentNames.Where(n => Cnt(n) == least).ToList();
                string? lastName = null;
                if (lastHuman != null && lastIsJobseeker)
                {
                    var spk = (lastHuman.Name ?? "").Replace("求職者-", "").Trim();
                    lastName = presentNames.FirstOrDefault(n => string.Equals(n, spk, StringComparison.OrdinalIgnoreCase));
                }
                // 🎯 除了「自我介紹」階段可以輪流換人，其他時候一律「上一個問題問誰，這次就問誰」：
                //    剛回答完的求職者 → 繼續問他；否則 → 問回 AI 上一題問的那位；都沒有才輪流。
                string? stickyName = lastName;
                if (stickyName == null)
                {
                    // 最近一位回答的求職者＝「上一個問題問的那位」
                    var lastJs = lines.LastOrDefault(l => l.Role != "ai" && (string.Equals(l.Role, "jobseeker", StringComparison.OrdinalIgnoreCase) || (l.Name ?? "").StartsWith("求職者")));
                    if (lastJs != null)
                    {
                        var jn = (lastJs.Name ?? "").Replace("求職者-", "").Trim();
                        stickyName = presentNames.FirstOrDefault(n => string.Equals(n, jn, StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (stickyName == null && _aiLast.TryGetValue(code, out var stickyAi) && stickyAi.Name != null && presentNames.Contains(stickyAi.Name)) stickyName = stickyAi.Name;
                // 🎯 中途才加入、或到現在還沒被問過也沒開口講過話的求職者：不能只圍繞一開始就在的人提問。
                //    （職員剛提了問題、等人回答的情況不動；原本問的那位已經被 AI 問過至少一次，就輪到這位）
                var spokenJs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in lines.Where(JsLine)) spokenJs.Add((l.Name ?? "").Replace("求職者-", "").Trim());
                if (_convo.TryGetValue(code, out var fullConvo)) { lock (fullConvo) foreach (var l in fullConvo.Where(JsLine)) spokenJs.Add((l.Name ?? "").Replace("求職者-", "").Trim()); }
                var unheard = presentNames.FirstOrDefault(n => Cnt(n) == 0 && !spokenJs.Contains(n));
                if (unheard != null && !scenario.StartsWith("情境A") && (stickyName == null || Cnt(stickyName) >= 1 || string.Equals(stickyName, unheard, StringComparison.OrdinalIgnoreCase)))
                {
                    targetName = unheard;
                    newcomerNote = $"\n【新加入／還沒被問過的求職者】「{unheard}」目前還沒被問過、也還沒開口。這一題請直接問他，題目要用「他自己的履歷」來出，不要延續前一位求職者的回答或內容。";
                    Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 「{unheard}」還沒被問過也沒講過話（可能是中途加入），這次改問他。");
                }
                else if (stickyName != null) targetName = stickyName;
                else targetName = leastNames[new Random().Next(leastNames.Count)];
            }
            // 🎯 模式判斷
            //   U（上一題沒人回答、30 秒後仍冷場）：同一位、同一題換個說法／更具體／給提示，不能原句重複，不換人
            //   N（其他：求職者已回答完等）：不再追問已回答的內容，也不重複任何問過的題目，問一個全新的題目
            var prevQs = _aiQuestions.GetOrAdd(code, _ => new List<string>());
            List<string> prevSnapshot; lock (prevQs) prevSnapshot = prevQs.ToList();
            bool modeU = false;
            if (req.AiUnanswered && _aiLast.TryGetValue(code, out var lastAi) && lastAi.Name != null && presentNames.Contains(lastAi.Name))
            {
                modeU = true;
                targetName = lastAi.Name;
                scenario = $"情境U：你（AI 面試官）剛才問「{lastAi.Name}」：「{lastAi.Text}」，過了 30 秒沒有人回答，職員也沒有接著問。請仍然只對「{lastAi.Name}」，針對同一個問題換個問法：更具體、更好回答，或提示可以從履歷中的哪一段經歷談起。不可以原句重複，也不可以換成別的題目或別的人。";
            }
            else if (lastIsJobseeker)
            {
                scenario = "情境N：剛剛的問題已經有人回答完了。不要重複問已經回答的內容，也不要重複任何問過的問題；但可以根據「上一個問題和他的回答」往下延伸（例如以他說的做法為背景，出一個「如果遇到……你會怎麼調整」的情境題），或依「目前主題」換一個不同角度、自然銜接下一步；不可以回到職員已經談完的較早主題（例如已經過了自我介紹，就不能再問自我介紹或履歷簡介）。履歷只用來讓問題更貼近這位求職者。";
            }
            var historyText = prevSnapshot.Count == 0 ? "（無）" : string.Join("\n", prevSnapshot.Select(q => "- " + q));
            if (introPhase && !modeU && targetName != null)
                scenario = $"情境I：會議一開始是在請大家自我介紹，還有求職者沒介紹到。請只對「{targetName}」說一句，邀請他也簡單做個自我介紹（例如「{targetName}，接下來也請你簡單自我介紹一下吧」）。不要問履歷或專業問題。";
            if (!modeU && !introPhase && newcomerNote.Length > 0) scenario += newcomerNote;
            string rule1 = modeU
                ? "1. 只對指定的那一位，針對你上次的問題換個說法，不可原句重複。"
                : (scenario.StartsWith("情境I")
                    ? "1. 只邀請指定的那位求職者簡單自我介紹，不要問別的。"
                : scenario.StartsWith("情境D")
                    ? "1. 不要追問剛結束的話題；請換一個新的方向，根據該求職者履歷裡的具體經歷出情境題，或問這個職缺上班時可能遇到的狀況。"
                : (scenario.StartsWith("情境N")
                    ? "1. 問題要緊扣「目前主題（職員最近一次的提問）」，可以根據上一個問題和回答延伸，或換一個新的角度或下一步；不要重複問剛剛已回答的內容，不要和「已問過的問題」相同或相近，也不可以回到「職員已談完的較早主題」。"
                    : "1. 你的問題必須直接延伸「最近一句人類發言」的話題（接著他剛說的內容往下問、深入追問或引導）。履歷只用來讓這個追問更具體，不可以丟開這句話、另外跳去問履歷裡不相干的事，也不可以換成新的主題。"));
            string rule8 = modeU
                ? "8. 不要換人、不要換題目：只能就上次那個問題換個角度。"
                : "8. 整場會議已經問過的問題一律不可重複或換句話說重問（見「已問過的問題」）。";
            var targetRule = targetName != null
                ? $"\n【指定對象】這句話只能對「{targetName}」說，句子以「{targetName}，」開頭；不可以改問其他求職者。\n"
                : (ambiguousStaffQ ? "\n【指定對象】職員剛提的問題沒聽清楚是問誰，這句話不要點任何人的名字，不要用「○○，」開頭；請就同一個問題換個說法，用「哪位想先分享」「請有經驗的人先談談」這類不指定對象的說法。\n" : "");
            var jobText = sbJob.Length > 0 ? sbJob.ToString() : "（無）";
            var resumeText = sbResume.Length > 0 ? sbResume.ToString() : "（無履歷資料）";
            var prompt =
$@"你是這場面試的 AI 面試官，與在場的職員（面試官）一起面試。現在對話安靜了一陣子，請判斷是否該開口。

【職缺】
{jobText}
【在場求職者與履歷】（姓名：{names}）
{resumeText}
【最近的對話】（由舊到新；「求職者-」「職員-」「主持人-」是身分前綴，AI 面試官是你自己）
{sbConvo}
最近一位發言者：{req.LastSpeakerName}（{req.LastSpeakerRole}）
【目前主題＝職員最近一次的提問／要求】
{themeText}
【職員較早問過的題目＝已談完的階段，不可以再回頭問】
{earlierStaff}
【最近一句人類發言】
{anchorText}
【整場已問過的問題（不可重複）】
{historyText}
【目前情境與做法】
{scenario}{targetRule}

規則：
{rule1}
2. 一次只問一個問題，口語自然，40 字以內。
3. 可以指定一位求職者：句子以該求職者的姓名開頭（只用姓名，不要「求職者-」前綴）。有多位求職者時，依剛剛的話題與各自履歷，挑最適合的一位，不要每個人問一樣的問題，也不要連續只問同一個人。
4. 只能引用被點名者本人的履歷或他自己說過的話，不要拿其他求職者的資料比較。
5. 禁止廢話：不要打氣、安撫（例如「不用緊張」「慢慢想」）、客套、總結剛剛的對話、重複問過的問題（情境A 例外：同一題可以換個說法再引導，但不能原句重複）、問履歷與對話都沒有依據的事。
6. 如果職員剛問了問題或提出要求、求職者還沒回答：請「就同一個問題」換個說法、縮小範圍，或提示可以從履歷中的哪段經歷談起，幫求職者開口。絕對不能趁機換到另一個話題。
7. 現在已經冷場，你一定要說一句話，不可以輸出 SKIP，要用一句具體的引導或追問接上目前主題。
11. 履歷上已經寫明的事實（公司名稱、職稱、做過的專案、學歷、證照）不要再問，更不要問「可以談談你在○○公司的經驗嗎」「說明你負責的項目」這種把履歷念一遍的表面問題；要根據履歷寫的具體經驗，出情境題或判斷題（例如以他做過的專案為背景：「如果遇到……狀況，你會怎麼處理、怎麼取捨？」），讓他說出履歷看不到的思考與做法。（情境U 與情境I 不適用此條。）
10. 題型可以多樣：除了延伸上一題與回答、依履歷追問之外，也可以問「這個職缺實際上班時可能會遇到的狀況」或出情境題（例如「如果……，你會怎麼處理？」「遇到……你會怎麼取捨？」）。情境題必須和職缺的工作內容、目前主題相關，具體、口語、40 字以內，不要出空泛或與職缺無關的題目。（情境U 只能換個說法問同一題，不適用此條。）
9. 自我介紹早就結束了：除非「目前主題」本身就是在請人自我介紹，否則題目裡絕對不可以出現「自我介紹」「介紹一下自己」「介紹自己」這類字眼，也不可以問履歷簡介；要問就問更深入、更專業的內容（做法、數字、判斷、取捨）。
{rule8}
只輸出要說的那一句話，不加引號、前綴或說明。";

            var prevForCheck = prevSnapshot.Concat(staffQTexts).ToList();
            var raw = await _gemini.AskAsync(prompt, "你是台灣企業面試的 AI 面試官，說話自然、直接、精簡，像真人面試官。", 600);
            var text = (raw ?? "").Trim().Trim('"', '「', '」', '“', '”').Trim();
            if (text.Length < 4 || text.Length > 90 || text.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 判斷不發言（回應：{(raw == null ? "null" : Clip(raw, 40))}）");
                // 冷場時不能一直沉默：模型沒給出可用的句子，就用保底引導（有點名求職者就點名）
                text = lastHuman != null && !lastIsJobseeker
                    ? (targetName != null ? $"{targetName}，可以先從你最有代表性的一段經歷開始說嗎？" : "可以先從最有代表性的一段經歷開始說嗎？")
                    : (targetName != null ? $"{targetName}，可以再多說明一下剛剛提到的做法和結果嗎？" : "可以再多說明一下剛剛提到的做法和結果嗎？");
            }
            // 🎯 禁止在自我介紹階段結束後又回頭問自我介紹（除非職員最近一次提問就是在請自我介紹）
            bool themeIsIntro = themeLine != null && System.Text.RegularExpressions.Regex.IsMatch(themeLine.Text ?? "", "自我介紹|介紹一下自己|介紹自己");
            // 只有「真的還有人沒自我介紹」的階段才准 AI 問自我介紹；其他時候（包含職員最近一次提問剛好是請人自我介紹，但人已經介紹完了）一律不准
            bool IntroAgain(string t) => !introPhase && IntroBroadRx.IsMatch(t ?? "");
            if (IntroAgain(text))
            {
                Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 產生的問題又回到自我介紹，重新產生：「{text}」");
                var rawI = await _gemini.AskAsync(prompt + "\n\n【重要】你剛才的問題「" + text + "」回頭問了自我介紹，面試早就進入專業問題了。請改問跟「目前主題」有關、更深入的專業細節（做法、數字、判斷、取捨），不可以出現「自我介紹」，只輸出那一句話。", "你是台灣企業面試的 AI 面試官，說話自然、直接、精簡，像真人面試官。", 600);
                var tI = (rawI ?? "").Trim().Trim('"', '「', '」', '“', '”').Trim();
                if (tI.Length >= 4 && tI.Length <= 90 && !IntroAgain(tI) && !tI.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase)) text = tI;
                else text = targetName != null ? $"{targetName}，剛剛談到的這件事，你當時最關鍵的判斷或做法是什麼？" : "剛剛談到的這件事，你當時最關鍵的判斷或做法是什麼？";
            }
            // 🎯 去重：和整場問過的題目太像就重生一次，還是太像就用保底題庫（情境U 只要求不是原句重複）
            double simTh = modeU ? 0.8 : 0.5;
            if (TooSimilar(text, prevForCheck, presentNames, simTh))
            {
                Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 產生的問題和之前太像，重新產生：「{text}」");
                var raw2 = await _gemini.AskAsync(prompt + "\n\n【重要】你剛才想的問題「" + text + "」和已問過的太像了，請換一個完全不同的問法／面向，只輸出那一句話。", "你是台灣企業面試的 AI 面試官，說話自然、直接、精簡，像真人面試官。", 600);
                var t2 = (raw2 ?? "").Trim().Trim('"', '「', '」', '“', '”').Trim();
                if (t2.Length >= 4 && t2.Length <= 90 && !t2.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase)) text = t2;
                if (TooSimilar(text, prevForCheck, presentNames, simTh))
                {
                    var pool = modeU
                        ? new[] { "換個角度，可以先舉一個你印象最深的實際例子嗎？", "如果不知道從哪開始，可以先說說你當時扮演的角色嗎？", "不用說得很完整，先講你做過的一件具體的事就好。" }
                        : new[] { "可以分享一次你在工作或專案中遇到最大的挑戰，以及你怎麼解決的嗎？", "你覺得自己最符合這個職位的一項能力是什麼？可以舉個例子嗎？", "如果加入團隊，你希望前三個月做出什麼貢獻？", "你有跟不同部門合作的經驗嗎？意見不同時你怎麼處理？", "最近你學到的一項新技能是什麼？怎麼運用的？", "你為什麼對這個職位有興趣？" };
                    var unused = pool.Where(c => !TooSimilar(c, prevForCheck, presentNames, simTh)).ToList();
                    var pick = unused.Count > 0 ? unused[new Random().Next(unused.Count)] : pool[new Random().Next(pool.Length)];
                    text = targetName != null ? $"{targetName}，{pick}" : pick;
                }
            }
            // 模型若無視指定對象、開頭點了別人，強制換成指定的人
            if (targetName != null)
            {
                foreach (var other in presentNames.Where(n => n != targetName))
                {
                    if (text.StartsWith(other, StringComparison.OrdinalIgnoreCase))
                    {
                        text = targetName + text.Substring(other.Length);
                        break;
                    }
                }
            }
            string? askedName = presentNames.FirstOrDefault(pn => text.StartsWith(pn, StringComparison.OrdinalIgnoreCase));
            _aiPending[code] = (text, askedName ?? targetName, modeU); // 等真的說出口（AiSpeak）才記進「已問過」
            Console.WriteLine($"[RoomController] AiNextQuestion：房間 {code} 產生提問：「{text}」");
            return Ok(new { text });
        }

        private async Task SpeakAndBroadcastAsync(string roomCode, string text)
        {
            try
            {
                await _hub.Clients.Group(roomCode).SendAsync("AiSpeakingStateChanged", true, text);
                await _botService.SpeakAsync(roomCode, text, async () => { AddConvo(roomCode, "AI 面試官", "ai", text); await _hub.Clients.Group(roomCode).SendAsync("AiSubtitle", text); if (_aiLast.TryGetValue(roomCode, out var al) && al.Text == text) await SetResponderAsync(roomCode, string.IsNullOrEmpty(al.Name) ? null : al.Name); }, async () => PersonSpeaking(roomCode) || ((await _botService.GetAudibleSilenceSecondsAsync(roomCode)) is double aq && aq < 1.2)); // 這裡面會等到真的講完（依音訊長度換算）才回傳
            }
            finally
            {
                // 不管上面成功還失敗，都要廣播「說完了」，不然全場的冷場計時器會永遠卡在暫停狀態
                MarkActivity(roomCode); // AI 講完＝新的安靜起點
                var interrupted = _botService.ConsumeInterrupted(roomCode);
                await _hub.Clients.Group(roomCode).SendAsync("AiSpeakingStateChanged", false, text, interrupted);
            }
        }

        // 🐛 這輪新增：給「結束會議」畫面用的真實狀態查詢，不要再無條件顯示「錄影已完成上傳」。
        //    AI 面試官加入會議、錄影上傳，都是背景執行的（見 MeetingHub.StartMeeting / LeaveRoomAsync 的
        //    fire-and-forget 工作），主持人按下「結束會議」的當下，錄影很可能都還沒上傳完——
        //    所以這裡會等一下（最多 2 分鐘，跟 SaveAiAnalysis 那邊等錄影的邏輯一致，
        //    本機測試時上傳頻寬常常比機房環境慢很多，40 秒不夠用），
        //    真的查到最後結果（成功有檔案 / AI 面試官加入失敗 / 逾時還沒完成）才回傳，讓前端能誠實顯示。
        [HttpGet]
        public async Task<IActionResult> GetRecordingStatus(string roomCode)
        {
            var room = await _context.Rooms.FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return NotFound();

            for (int i = 0; i < 120; i++)
            {
                if (!string.IsNullOrEmpty(room.RecordingFileName) || !string.IsNullOrEmpty(room.AiBotErrorMessage))
                    break;
                await Task.Delay(1000);
                await _context.Entry(room).ReloadAsync();
            }

            return Ok(new
            {
                success = true,
                recordingFileName = room.RecordingFileName,
                aiBotErrorMessage = room.AiBotErrorMessage
            });
        }

        // 🎯 逐字稿改存到 Cloudflare R2，不再存本機 wwwroot
        //    這樣本機執行跟部署到 Render，讀到的都是同一份雲端檔案，不會再有兩邊結果不一致的問題
        //
        //    🐛 這輪大改：原本「合併 TranscriptChunks 表裡每個人各自送來的片段」這套機制，
        //    追了快 15 輪，每次都是新的失敗模式（audio-capture、no-speech、network、
        //    永遠只有主持人成功）——問題根源是瀏覽器端語音辨識這條路徑本質上不可靠。
        //    改成主要依賴「整場會議錄影」直接讓 Gemini 產生逐字稿：同一份錄影本來就會拿去給
        //    AnalyzeInterviewVideoAsync 做多模態分析，這裡用同樣的方式上傳一次，讓 Gemini
        //    直接看畫面（每個人名字都有標籤）+ 聽聲音，產生完整逐字稿，不再依賴任何人自己
        //    瀏覽器端的麥克風/語音辨識。
        //    TranscriptChunks 那套機制保留當作「影片還沒準備好/轉錄失敗」時的備援，不會整個刪掉，
        //    確保就算這條新路徑失敗，舊機制還有機會撿到一些內容，不會直接變成完全空白。
        [HttpPost]
        public async Task<IActionResult> SaveTranscript([FromForm] string roomCode)
        {
            var room = await _context.Rooms.Include(r => r.Job).FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return NotFound();

            string? content = null;
            bool isEmpty;

            // 🎯 錄影上傳是背景工作（結束會議後 AI 面試官才離開、轉檔、上傳），這裡跟 SaveAiAnalysis
            //    用同樣的等待邏輯——最多等 2 分鐘，本機測試上傳頻寬常常比較慢，等不到才放棄改用備援。
            string? recordingFileName = room.RecordingFileName;
            for (int i = 0; i < 120 && string.IsNullOrEmpty(recordingFileName); i++)
            {
                await Task.Delay(1000);
                await _context.Entry(room).ReloadAsync();
                recordingFileName = room.RecordingFileName;
            }

            if (!string.IsNullOrEmpty(recordingFileName) && _storage.IsConfigured)
            {
                string? geminiFileUri = null;
                string? geminiFileName = null;
                try
                {
                    var videoBytes = await _storage.DownloadBytesAsync($"錄影錄音/{recordingFileName}");
                    if (videoBytes.Length > 0 && videoBytes.Length < 200_000_000)
                    {
                        (geminiFileUri, geminiFileName) = await _gemini.UploadFileAsync(videoBytes, "video/webm", recordingFileName);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SaveTranscript] 房間 {roomCode} 下載/上傳錄影檔給 Gemini 失敗，改用備援機制：{ex.Message}");
                }

                if (!string.IsNullOrEmpty(geminiFileUri))
                {
                    // 🎯 把這個房間所有已知參與者的名字整理出來，當提示給 Gemini 對照畫面上的名字標籤
                    var participants = await _context.RoomParticipants
                        .Where(p => p.RoomId == room.Id && (p.Role == ParticipantRole.AI || p.JoinedAt != null))
                        .Include(p => p.Resume).ThenInclude(r => r!.Member)
                        .Include(p => p.Employee)
                        .ToListAsync();
                    var participantNames = participants.Select(p =>
                    {
                        if (p.Role == ParticipantRole.AI) return "AI 面試官";
                        var personName = p.Role == ParticipantRole.Jobseeker ? p.Resume?.Member?.Name : p.Employee?.Name;
                        if (string.IsNullOrEmpty(personName)) return null;
                        var roleLabel = p.Role switch
                        {
                            ParticipantRole.Manager => (room.HostEmployeeId != null && p.EmployeeId == room.HostEmployeeId) ? "主持人" : "職員",
                            ParticipantRole.Jobseeker => "求職者",
                            _ => null
                        };
                        return roleLabel != null ? $"{roleLabel}-{personName}" : personName;
                    }).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList()!;

                    // 🎯 有「各人自己麥克風」的逐字稿片段就不用影片猜講者了（影片轉錄是靠畫面/聲音猜誰在講，最容易認錯人，
                    //    而且要多花 1~2 分鐘）；只有整場完全沒有任何片段時才退回影片轉錄。
                    var hasOwnChunks = await _context.TranscriptChunks.AnyAsync(c => c.RoomCode == roomCode && c.Speaker != "AI 面試官");
                    if (!hasOwnChunks)
                    {
                        content = await _gemini.TranscribeInterviewVideoAsync(geminiFileUri, "video/webm", participantNames!);
                        if (content == null)
                        {
                            await Task.Delay(2000);
                            content = await _gemini.TranscribeInterviewVideoAsync(geminiFileUri, "video/webm", participantNames!);
                        }
                    }
                    else Console.WriteLine($"[SaveTranscript] 房間 {roomCode}：已有各人自己麥克風的片段，略過影片轉錄（避免認錯講者、加快處理）。");

                    // 不在這裡刪：留給 SaveAiAnalysis 沿用同一份上傳，省掉第二次下載+上傳
                    if (!string.IsNullOrEmpty(geminiFileName)) _geminiVideoCache[roomCode] = (geminiFileUri, geminiFileName, DateTime.UtcNow);
                }
            }

            // 🐛 這輪修正（回報：逐字稿時間格式對了，但「所有角色應該照時間排序，而不是按角色分開」）：
            //    原本是把影片轉錄的內容整段放前面，TranscriptChunks 的備援內容整段接在後面，
            //    等於是「先列完一個人再列另一個人」，不是真正照時間先後交錯排列。
            //    改成把兩邊的內容都拆成一行一行、各自帶著真正的時間戳記，混在一起依時間重新排序，
            //    才會是「誰先講就先列誰」的正確順序。
            //    影片轉錄的內容用的是「影片開頭 00:00 起算」的相對時間，這裡用會議開始時間
            //    （room.StartAt）當基準換算成真正的時鐘時間，這樣才能跟 TranscriptChunks 的
            //    ReceivedAt（真正的 DateTime，不是猜的）放在同一個時間軸上比較、排序。
            var merged = new List<(DateTime time, string line)>();

            var chunks = await _context.TranscriptChunks
                .Where(c => c.RoomCode == roomCode)
                .OrderBy(c => c.ReceivedAt)
                .ToListAsync();

            // 🎯 沒有真正進入會議（沒有進場紀錄）的人，不可能有發言：他們的頁面可能只是開著、
            //    麥克風收到現場別人的聲音，被錯記成他說的。這類紀錄一律剔除。
            {
                var allParts = await _context.RoomParticipants.AsNoTracking()
                    .Where(p => p.RoomId == room.Id && p.Role != ParticipantRole.AI)
                    .Include(p => p.Resume).ThenInclude(r => r!.Member)
                    .Include(p => p.Employee)
                    .ToListAsync();
                var notJoined = new HashSet<string>();
                foreach (var p in allParts.Where(p => p.JoinedAt == null))
                {
                    var nm = p.Role == ParticipantRole.Jobseeker ? p.Resume?.Member?.Name : p.Employee?.Name;
                    if (string.IsNullOrEmpty(nm)) continue;
                    notJoined.Add(nm);
                    notJoined.Add($"求職者-{nm}"); notJoined.Add($"職員-{nm}"); notJoined.Add($"主持人-{nm}");
                }
                var before = chunks.Count;
                chunks = chunks.Where(c => !notJoined.Contains((c.Speaker ?? "").Trim())).ToList();
                if (before != chunks.Count)
                    Console.WriteLine($"[SaveTranscript] 房間 {roomCode}：剔除 {before - chunks.Count} 筆未進場者的發言紀錄。");
            }

            // 🐛 這輪新增：發現一種前面的雜訊判斷完全抓不到的洗版模式——SubmitAudioTranscript
            //    這條新路徑是「整段音檔一次轉錄成一整行」，如果麥克風整場收到的幾乎都是同一個
            //    語助詞（例如「嗯」），Gemini 可能會原封不動轉出一整行幾百字的「嗯嗯嗯…嗯」。
            //    這種情況下面那個「同一句 5 字以內的話反覆出現超過一半」的判斷完全抓不到——
            //    因為它是『一整行』，不是『很多筆 5 字以內的短行』，`t.Length <= 5` 那個篩選條件
            //    直接把它排除在外，等於完全沒被檢查過就直接被當成正常內容用了。
            //    改成在這裡先逐筆檢查每一行本身：如果這一行去掉空白後夠長（>=20 字），
            //    但裡面出現的「不同字元種類」少到誇張（<=3 種），幾乎可以確定是同一兩個字
            //    重複堆出來的雜訊（正常語句用字非常多樣，不可能一整段只用不到 4 種字），
            //    直接把這一筆從 chunks 裡剔除，不讓它有機會混進最終逐字稿。
            bool LooksLikeRepeatedFillerSpam(string text)
            {
                var chars = text.Where(ch => !char.IsWhiteSpace(ch)).ToList();
                if (chars.Count < 20) return false;
                var distinct = chars.Distinct().Count();
                return distinct <= 3;
            }
            var spamFilteredChunks = chunks.Where(c => !LooksLikeRepeatedFillerSpam(c.Text)).ToList();
            var droppedAsSpam = chunks.Count - spamFilteredChunks.Count;
            if (droppedAsSpam > 0)
            {
                Console.WriteLine($"[SaveTranscript] roomCode={roomCode}：有 {droppedAsSpam} 筆內容被判定成單行雜訊洗版（例如一整行都是重複的「嗯」），已直接剔除不放進逐字稿。");
            }
            chunks = spamFilteredChunks;

            // 🎯 講者歸屬規則（最重要）：
            //    1. 有「即時整句轉錄」片段（TimeLabel 帶 |p=音量）的人，只信這些片段，他其他來源（整段音檔、瀏覽器辨識）的片段丟掉。
            //    2. 同一句話被好幾支麥克風收到（同一個房間、喇叭外放），只留「音量最大那支麥克風」的主人，其餘當成串音丟掉。
            {
                double PeakOf(TranscriptChunkRecord c)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(c.TimeLabel ?? "", @"\|p=([0-9\.]+)");
                    return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1;
                }
                var liveSpeakers = new HashSet<string>(chunks.Where(c => PeakOf(c) >= 0).Select(c => c.Speaker ?? ""));
                var b0 = chunks.Count;
                chunks = chunks.Where(c => PeakOf(c) >= 0 || (c.Speaker ?? "") == "AI 面試官" || !liveSpeakers.Contains(c.Speaker ?? "")).ToList();
                var lives = chunks.Where(c => PeakOf(c) >= 0).ToList();
                var dropBleed = new HashSet<int>();
                foreach (var c in chunks)
                {
                    if ((c.Speaker ?? "") == "AI 面試官") continue;
                    var pc = PeakOf(c);
                    foreach (var o in lives)
                    {
                        if (o.Id == c.Id || (o.Speaker ?? "") == (c.Speaker ?? "")) continue;
                        if (Math.Abs((o.ReceivedAt - c.ReceivedAt).TotalSeconds) > 10) continue;
                        if (BigramSim(c.Text, o.Text) < 0.4) continue;
                        if (PeakOf(o) > pc) { dropBleed.Add(c.Id); break; }
                    }
                }
                chunks = chunks.Where(c => !dropBleed.Contains(c.Id)).ToList();
                if (b0 != chunks.Count) Console.WriteLine($"[SaveTranscript] 房間 {roomCode}：丟掉 {b0 - chunks.Count} 筆非本人麥克風來源／串音的片段（共 {dropBleed.Count} 筆為串音）。");
            }

            // 🐛 這輪修正（回報：「主持人還不如 TranscriptChunks 準確」）：
            //    主持人自己裝置錄音轉出來的內容，一直都比影片轉錄準（影片轉錄是從整場會議合成畫面
            //    去猜聲音內容，音質/畫質都打了折扣，難免比較容易聽錯）。原本兩邊內容不分青紅皂白
            //    全部混在一起，同一個人講的話可能兩邊各出現一次、內容還兜不起來，反而讓人搞不清楚
            //    哪個才是準的。改成：誰在 TranscriptChunks 裡本來就有內容（代表這個人自己裝置的
            //    錄音/辨識有成功），就完全信任那份、影片轉錄裡屬於同一個人的行全部捨棄不用；
            //    影片轉錄只拿來補「TranscriptChunks 完全沒有内容」的人（目前最常見就是求職者）。
            //
            //    🐛 這輪再修正：發現「有內容」不代表「內容可信」——如果麥克風收到的是雜音/呼吸聲，
            //    瀏覽器語音辨識可能會把同一個很短的字（例如「嗯」）連續好幾分鐘、每一兩秒就誤判成
            //    一次新的正式結果，這種情況下 TranscriptChunks 裡「有很多筆」反而是雜訊洗出來的，
            //    比影片轉錄還不可信。加一個簡單的品質判斷：如果這個人的內容裡，同一句很短的話
            //    （5 字以內）反覆出現超過一半，就當作是雜訊洗版，不信任這份、改用影片轉錄的內容。
            var speakersWithChunks = new HashSet<string>();
            foreach (var group in chunks.GroupBy(c => c.Speaker))
            {
                var textsInGroup = group.Select(c => c.Text.Trim()).ToList();
                var mostCommonCount = textsInGroup
                    .Where(t => t.Length <= 5)
                    .GroupBy(t => t)
                    .Select(g => g.Count())
                    .DefaultIfEmpty(0)
                    .Max();
                var looksLikeSpam = textsInGroup.Count >= 6 && mostCommonCount >= textsInGroup.Count / 2;
                if (!looksLikeSpam) speakersWithChunks.Add(group.Key);
            }

            // 🎯 影片轉錄只在「完全沒有任何片段」時才用，而且講者必須是實際進場的人（或 AI），名字對不上的行整行丟掉
            HashSet<string>? joinedNamesForVideo = null;
            if (chunks.Count == 0 && !string.IsNullOrEmpty(content))
            {
                var jp = await _context.RoomParticipants.AsNoTracking()
                    .Where(p => p.RoomId == room.Id && p.Role != ParticipantRole.AI && p.JoinedAt != null)
                    .Include(p => p.Resume).ThenInclude(r => r!.Member).Include(p => p.Employee).ToListAsync();
                joinedNamesForVideo = new HashSet<string>(jp.Select(p => p.Role == ParticipantRole.Jobseeker ? p.Resume?.Member?.Name : p.Employee?.Name)
                    .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!));
            }
            if (chunks.Count == 0 && !string.IsNullOrEmpty(content) && room.StartAt.HasValue && joinedNamesForVideo != null)
            {
                var baseTime = room.StartAt.Value;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    content, @"^\[(\d{1,2}):(\d{2})\]\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline))
                {
                    var rest = m.Groups[3].Value.Trim();
                    var colonIdx = rest.IndexOf('：');
                    var speakerName = colonIdx > 0 ? rest.Substring(0, colonIdx).Trim() : null;

                    // 這個人已經有自己裝置錄音的內容了，影片轉錄這行就跳過，避免同一句話出現兩個版本
                    if (speakerName != null && speakersWithChunks.Contains(speakerName)) continue;
                    // 講者不是實際進場的人（也不是 AI）→ 一定是認錯人，整行丟掉
                    if (speakerName == null) continue;
                    if (speakerName != "AI 面試官" && !joinedNamesForVideo.Any(n => speakerName == n || speakerName.EndsWith("-" + n))) continue;

                    var minutes = int.Parse(m.Groups[1].Value);
                    var seconds = int.Parse(m.Groups[2].Value);
                    var lineTime = baseTime.AddMinutes(minutes).AddSeconds(seconds);
                    merged.Add((lineTime, $"[{lineTime:tt h:mm:ss}] {rest}"));
                }
            }

            // 🎯 同一個人的同一句話可能同時來自「瀏覽器即時辨識」和「整段音檔轉錄」兩個來源，
            //    時間相近（20 秒內）且文字相同／包含關係的視為同一句，只留一筆，避免逐字稿重複。
            var acceptedChunks = new List<(string speaker, string norm, DateTime time)>();
            string NormText(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"[\s。，、！？…\.\,\!\?：:；;「」\""']", "");
            foreach (var c in chunks.OrderBy(x => x.ReceivedAt))
            {
                // 🎯 被判定是雜訊洗版的人，這些原始片段就不要輸出了——影片轉錄那邊已經補上這個人的內容了
                if (!speakersWithChunks.Contains(c.Speaker)) continue;
                var nrm = NormText(c.Text);
                if (nrm.Length >= 3 && acceptedChunks.Any(a => a.speaker == c.Speaker
                        && Math.Abs((a.time - c.ReceivedAt).TotalSeconds) <= 20
                        && (a.norm == nrm || (a.norm.Length >= 4 && nrm.Length >= 4 && (a.norm.Contains(nrm) || nrm.Contains(a.norm))))))
                    continue;
                acceptedChunks.Add((c.Speaker, nrm, c.ReceivedAt));
                var text = c.Text.Replace("\r\n", " ").Replace("\n", " ").Trim();
                merged.Add((c.ReceivedAt, $"[{c.ReceivedAt:tt h:mm:ss}] {c.Speaker}：{text}"));
            }

            // 🎯 清掉不該出現的行：
            //    1. 沒有內容的行（例如影片分析產生的「不明講者：」空行）
            //    2. 回音：別人的麥克風會收到 AI 面試官從喇叭傳出的聲音，被當成那個人說的話。
            //       如果某一行（非 AI）的內容，被「30 秒內的 AI 面試官發言」完整包含，就視為回音，丟掉。
            {
                var lineRx = new System.Text.RegularExpressions.Regex(@"^\[[^\]]+\]\s*([^：]+)：(.*)$");
                string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"[\s。，、！？…\.\,\!\?：:；;「」\""']", "");
                var aiLines = new List<(DateTime time, string norm)>();
                foreach (var (time, line) in merged)
                {
                    var mm = lineRx.Match(line);
                    if (mm.Success && mm.Groups[1].Value.Trim() == "AI 面試官") aiLines.Add((time, Norm(mm.Groups[2].Value)));
                }
                var before = merged.Count;
                merged = merged.Where(x =>
                {
                    var mm = lineRx.Match(x.line);
                    if (!mm.Success) return true;
                    var who = mm.Groups[1].Value.Trim();
                    var norm = Norm(mm.Groups[2].Value);
                    if (norm.Length == 0) return false;
                    if (who == "AI 面試官") return true;
                    if (norm.Length >= 2 && aiLines.Any(a => Math.Abs((a.time - x.time).TotalSeconds) <= 30 && a.norm.Contains(norm))) return false;
                    return true;
                }).ToList();
                if (merged.Count != before) Console.WriteLine($"[SaveTranscript] roomCode={roomCode}：清掉 {before - merged.Count} 行空白/AI 回音。");
            }

            if (merged.Count > 0)
            {
                // 🎯 嚴格依「逐字稿上顯示的時間」排序（時分秒），不依賴完整日期——
                //    不同來源（瀏覽器即時辨識、整段音檔轉錄、AI 開場白）算出來的 DateTime 如果日期/時區有細微差異，
                //    直接比 DateTime 會讓整批內容排到另一批的前面或後面（逐字稿就會一段一段亂跳）。
                //    以會議第一句話前 1 分鐘當起點，跨過午夜也不會亂。
                var firstTod = merged.Min(x => x.time.TimeOfDay) - TimeSpan.FromMinutes(1);
                if (merged.Max(x => x.time.TimeOfDay) - merged.Min(x => x.time.TimeOfDay) > TimeSpan.FromHours(12))
                    firstTod = TimeSpan.Zero; // 範圍跨午夜時，退回一般的時間順序
                Func<DateTime, double> tKey = d => ((d.TimeOfDay - firstTod).TotalSeconds + 86400) % 86400;
                content = string.Join("\n", merged
                    .OrderBy(x => tKey(x.time))
                    .ThenBy(x => x.time)
                    .Select(x => x.line));
                isEmpty = false;
            }
            else
            {
                content = "（本場會議未能產生逐字稿內容，可能原因：錄影尚未準備好、上傳給 Gemini 分析失敗，或會議全程沒有可辨識的人聲）";
                isEmpty = true;
            }

            var fileName = BuildFileName(room, "txt");
            try
            {
                await _storage.UploadTextAsync($"逐字稿/{fileName}", content);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = "逐字稿上傳到雲端儲存失敗：" + ex.Message });
            }

            // 🎯 不管這次逐字稿是用哪條路徑產生的，TranscriptChunks 這張暫存表裡屬於這個房間的資料
            //    都可以清掉了——內容已經併進上面的逐字稿了，留著只會變成下次同一個房間代碼
            //    重測時的殘留干擾
            var leftoverChunks = _context.TranscriptChunks.Where(c => c.RoomCode == roomCode);
            _context.TranscriptChunks.RemoveRange(leftoverChunks);

            room.TranscriptFileName = fileName;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, fileName, content, isEmpty });
        }

        // 🎯 錄影錄音改存到 Cloudflare R2
        [HttpPost]
        [RequestSizeLimit(500_000_000)] // 放寬到 500MB，避免長時間會議的錄影檔案被擋掉
        public async Task<IActionResult> SaveRecording([FromForm] string roomCode, IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest();

            var room = await _context.Rooms.Include(r => r.Job).FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return NotFound();

            var fileName = BuildFileName(room, "webm");
            try
            {
                using var stream = file.OpenReadStream();
                await _storage.UploadStreamAsync($"錄影錄音/{fileName}", stream, "video/webm");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = "錄影上傳到雲端儲存失敗：" + ex.Message });
            }

            room.RecordingFileName = fileName;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, fileName });
        }

        // 🎯 AI 面試分析結果改存到 Cloudflare R2，而且改成「每位求職者各自分析、各自存一份檔案」
        //    （不再是所有人塞進同一次 Gemini 呼叫裡分析，那樣字數會被瓜分，容易被截斷到講一半）
        //
        //    🐛 這輪修正兩個問題：
        //    1. 「有沒有參加面試」原本是用 transcript.Contains(candidateName) 判斷，
        //       但逐字稿本來就可能因為轉錄失敗/被過濾掉而缺漏某人的發言，導致明明有參加卻被誤判成沒參加。
        //       改成看 RoomParticipants 這張表的 JoinedAt（他真的進過會議室的紀錄），這才是可信的事實來源。
        //    2. AI 分析不能只靠逐字稿文字——逐字稿只有「說了什麼」，完全看不出語氣、表情。
        //       改成優先用「錄影檔（多模態：畫面表情 + 聲音語氣）+ 逐字稿」一起送給 Gemini 分析；
        //       如果錄影還沒上傳完成（AI 面試官錄影上傳是背景工作，可能還在跑）或上傳/分析失敗，
        //       才退回原本純文字逐字稿的分析方式，確保這個功能不會因為多模態那條路失敗就整個掛掉。
        [HttpPost]
        public async Task<IActionResult> SaveAiAnalysis([FromForm] string roomCode, [FromForm] string transcript)
        {
            var room = await _context.Rooms.Include(r => r.Job).FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return NotFound();

            // 這場面試受邀的所有求職者（一個房間理論上可能不只一位）
            var jobseekers = await _context.RoomParticipants
                .Where(p => p.RoomId == room.Id && p.Role == ParticipantRole.Jobseeker && p.ResumeId != null)
                .Include(p => p.Resume).ThenInclude(r => r!.Member)
                .ToListAsync();

            if (jobseekers.Count == 0)
                return Ok(new { success = true, files = new List<object>() });

            // 🎯 錄影上傳是背景工作（EndMeeting 觸發後 AI 面試官才離開會議、轉檔、上傳），
            //    很可能還沒做完就走到這裡了。短輪詢等它一下，等不到才放棄多模態、退回純文字分析。
            //    🐛 這輪拉長：原本只等 40 秒，實測發現本機環境（不是部署在機房，上傳頻寬較差）
            //    常常錄影上傳花超過 40 秒還沒好，導致明明錄影後來有成功，AI 分析卻已經先放棄、退回純文字，
            //    白白浪費了多模態分析的機會。拉長到最多等 2 分鐘，比較符合實際本機測試的網路狀況。
            string? recordingFileName = room.RecordingFileName;
            for (int i = 0; i < 120 && string.IsNullOrEmpty(recordingFileName); i++)
            {
                await Task.Delay(1000);
                await _context.Entry(room).ReloadAsync();
                recordingFileName = room.RecordingFileName;
            }
            if (string.IsNullOrEmpty(recordingFileName))
            {
                Console.WriteLine($"[SaveAiAnalysis] 房間 {roomCode} 等了 2 分鐘還是沒等到錄影檔案，退回純文字分析（AiBotErrorMessage：{room.AiBotErrorMessage ?? "(無)"}）");
            }

            // 🎯 把錄影檔上傳到 Gemini File API 一次就好（同一支影片，每位求職者都能重複參照同一個 file_uri），
            //    避免每位求職者都重傳一次整支影片、浪費時間跟流量
            string? geminiFileUri = null;
            string? geminiFileName = null;
            if (_geminiVideoCache.TryRemove(roomCode, out var cachedVideo) && (DateTime.UtcNow - cachedVideo.at).TotalHours < 24)
            {
                geminiFileUri = cachedVideo.uri;
                geminiFileName = cachedVideo.name;
            }
            else if (!string.IsNullOrEmpty(recordingFileName) && _storage.IsConfigured)
            {
                try
                {
                    var videoBytes = await _storage.DownloadBytesAsync($"錄影錄音/{recordingFileName}");
                    // Gemini File API 單檔上限是 2GB，但免費額度/請求逾時考量下，太大的檔案還是直接放棄多模態、退回文字分析比較穩
                    if (videoBytes.Length > 0 && videoBytes.Length < 200_000_000)
                    {
                        (geminiFileUri, geminiFileName) = await _gemini.UploadFileAsync(videoBytes, "video/webm", recordingFileName);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SaveAiAnalysis] 下載/上傳錄影檔給 Gemini 失敗，退回純文字分析：{ex.Message}");
                }
            }

            var results = new System.Collections.Concurrent.ConcurrentBag<object>();
            var scoreBag = new System.Collections.Concurrent.ConcurrentBag<CandScore>();

            // 🎯 實際進場的人名單，給影片分析辨識畫面上是誰用
            var joinedRoster = (await _context.RoomParticipants.AsNoTracking()
                    .Where(p => p.RoomId == room.Id && p.Role != ParticipantRole.AI && p.JoinedAt != null)
                    .Include(p => p.Resume).ThenInclude(r => r!.Member).Include(p => p.Employee).ToListAsync())
                .Select(p => p.Role == ParticipantRole.Jobseeker ? "求職者-" + p.Resume?.Member?.Name : "職員/主持人-" + p.Employee?.Name)
                .Where(n => !n.EndsWith("-")).ToList();

            // 多位求職者同時分析（只用 _storage/_gemini，不碰 DbContext，可安全並行）
            var cleanTx = CleanTranscript(transcript);
            async Task AnalyzeOne(RoomParticipant jobseeker)
            {
                // 🎯 候選人之間加一點間隔，避免連續呼叫 Gemini API 太快撞到免費額度的速率限制
                //    （這很可能就是「有些候選人分析成功、有些失敗」的真正原因，不是金鑰設定問題——
                //    金鑰是伺服器端統一設定的同一組，不會因為誰登入而不同）
                var candidateName = jobseeker.Resume?.Member?.Name ?? $"求職者{jobseeker.Id}";

                // 🎯 用 RoomParticipants 的實際進場紀錄判斷有沒有參加，而不是硬找逐字稿裡有沒有出現名字
                //    （逐字稿本來就可能缺漏，用字串比對會把「有參加但逐字稿沒收好」的人誤判成沒參加）
                bool actuallyAttended = jobseeker.Status == ParticipantStatus.Admitted && jobseeker.JoinedAt != null;

                if (!actuallyAttended)
                {
                    var noShowMsg = $"求職者{candidateName}並未參加此次面試會議（會議紀錄中沒有他的進場紀錄），無法針對其面試表現進行 AI 分析與評分。";
                    var noShowFileName = BuildFileName(room, "txt", candidateName);
                    try
                    {
                        await _storage.UploadTextAsync($"AI分析/{noShowFileName}", noShowMsg);
                        results.Add(new { candidateName, success = true, fileName = noShowFileName });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { candidateName, success = false, message = "上傳到雲端儲存失敗：" + ex.Message });
                    }
                    return;
                }

                string? analysis = null;
                var rubricText = BuildRubricGuidance(roomCode, candidateName);

                // 🎯 優先走多模態（看畫面表情、聽聲音語氣），只有在錄影不可用時才退回純文字
                //
                // 🐛 這輪修正：maxTokens 原本只有 3000，使用者回報「AI分析並沒有完全分析完就沒了」——
                //    這份分析要求 6 個段落（語氣、表情、答題邏輯、評分、錄取建議、整體評語），
                //    尤其多模態分析還要描述實際觀察到的畫面細節，中文字數換算下來 3000 tokens
                //    很容易在寫到第 5、6 段的時候就被硬生生截斷，不是內容本身有問題，是額度給太少。
                //    拉高到 8000，讓 Gemini 有足夠空間把 6 個段落真的寫完。
                if (!string.IsNullOrEmpty(geminiFileUri))
                {
                    analysis = await _gemini.AnalyzeInterviewVideoAsync(geminiFileUri, "video/webm", candidateName, cleanTx, maxTokens: 12000, joinedRoster, rubricText);
                    if (analysis == null)
                    {
                        await Task.Delay(2000);
                        analysis = await _gemini.AnalyzeInterviewVideoAsync(geminiFileUri, "video/webm", candidateName, cleanTx, maxTokens: 12000, joinedRoster, rubricText);
                    }
                    // 輸出不是完整的中文報告（例如夾帶英文思考過程、被截斷）就視為失敗，走重試/退回純文字
                    if (analysis != null && !LooksLikeChineseAnalysis(analysis))
                    {
                        Console.WriteLine($"[SaveAiAnalysis] {candidateName} 影片分析輸出不是完整中文報告，改用純文字分析。");
                        analysis = null;
                    }
                }

                if (analysis == null)
                {
                    // 🎯 沒有錄影可用，或多模態分析失敗兩次，退回原本純文字逐字稿分析（確保這功能不會整個掛掉）
                    var prompt =
                        $"以下是一場面試的完整逐字稿（可能包含多位求職者，發言前有標示姓名），" +
                        $"請只針對「{candidateName}」這位求職者的發言與表現進行分析（繁體中文，不用分析其他人）：\n\n{cleanTx}\n\n" +
                        $"（這次沒有錄影畫面，只能依逐字稿文字判斷；時間標記請寫逐字稿上的時間，並註明這是現實時間。）\n\n{rubricText}";

                    analysis = await _gemini.AskAsync(
                        prompt,
                        "你是專業人資顧問，說繁體中文，格式清晰條列，內容要完整寫完，不能寫到一半就停。",
                        maxTokens: 12000);

                    if (analysis == null)
                    {
                        await Task.Delay(2000);
                        analysis = await _gemini.AskAsync(
                            prompt,
                            "你是專業人資顧問，說繁體中文，格式清晰條列，內容要完整寫完，不能寫到一半就停。",
                            maxTokens: 8000);
                    }
                }

                if (analysis == null)
                {
                    results.Add(new { candidateName, success = false, message = "AI 分析失敗（重試一次後仍失敗，可能是 API 額度用完或速率限制）" });
                    return;
                }

                var (finalText, cscore) = FinalizeAnalysis(analysis, candidateName);
                if (cscore == null)
                {
                    cscore = await ScoreFromTextAsync(finalText, candidateName);
                    if (cscore != null) finalText = AppendScoreDetail(finalText, cscore, "依上述分析內容補算");
                }
                if (cscore == null)
                {
                    cscore = ScoreFromLiveNotes(roomCode, candidateName);
                    if (cscore != null) finalText = AppendScoreDetail(finalText, cscore, "依會議中即時記錄估算（參考用）");
                }
                if (cscore != null) scoreBag.Add(cscore);
                analysis = finalText.TrimEnd() + "\n\n" + FaceWatchService.BuildReport(roomCode, candidateName); // 臉部觀察（不計入評分）
                var fileName = BuildFileName(room, "txt", candidateName);
                try
                {
                    await _storage.UploadTextAsync($"AI分析/{fileName}", analysis);
                    results.Add(new { candidateName, success = true, fileName });
                }
                catch (Exception ex)
                {
                    results.Add(new { candidateName, success = false, message = "上傳到雲端儲存失敗：" + ex.Message });
                }
            }
            await Task.WhenAll(jobseekers.Select(AnalyzeOne));

            // 🎯 會議結束後：把各求職者的加權分數放在一起比較，輸出一份比較總表
            if (scoreBag.Count >= 2)
            {
                try
                {
                    var cmpText = await BuildComparisonAsync(room, scoreBag.ToList());
                    var cmpName = BuildFileName(room, "txt", "比較總表");
                    await _storage.UploadTextAsync($"AI分析/{cmpName}", cmpText);
                    results.Add(new { candidateName = "比較總表", success = true, fileName = cmpName });
                }
                catch (Exception ex) { Console.WriteLine("[SaveAiAnalysis] 比較總表產生失敗：" + ex.Message); }
            }
            _liveNotes.TryRemove(roomCode, out _);
            FaceWatchService.Clear(roomCode);

            // 🎯 這場分析用的暫存影片檔在 Gemini 端可以清掉了，不用等 48 小時自動過期
            if (!string.IsNullOrEmpty(geminiFileName))
            {
                _ = _gemini.DeleteFileAsync(geminiFileName);
            }

            return Ok(new { success = true, files = results.ToList() });
        }

        // 🎯 檔案總管：列出 R2 上「逐字稿」「錄影錄音」「AI分析」三個前綴底下目前存了哪些檔案，並提供下載連結
        //    只有 HR/職員能看（求職者不用看到這個）
        public async Task<IActionResult> Files()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager")
                return RedirectToAction("Index", "Login");

            if (!_storage.IsConfigured)
            {
                ViewBag.NotConfigured = true;
                ViewBag.Transcripts = new List<string?>();
                ViewBag.Recordings = new List<string?>();
                ViewBag.AiAnalyses = new List<string?>();
                return View("~/Views/Room/Files.cshtml");
            }

            var transcriptKeys = await _storage.ListKeysAsync("逐字稿/");
            var recordingKeys = await _storage.ListKeysAsync("錄影錄音/");
            var aiKeys = await _storage.ListKeysAsync("AI分析/");

            ViewBag.Transcripts = transcriptKeys.Select(k => k.Substring("逐字稿/".Length)).ToList();
            ViewBag.Recordings = recordingKeys.Select(k => k.Substring("錄影錄音/".Length)).ToList();
            ViewBag.AiAnalyses = aiKeys.Select(k => k.Substring("AI分析/".Length)).ToList();

            return View("~/Views/Room/Files.cshtml");
        }

        // 下載指定「資料夾」（R2 key 前綴）裡的檔案（folder 只允許這三個固定值，避免被拿去讀取其他任意路徑）
        // 直接 302 導向 R2 的簽名網址，不用把大檔案的位元組再繞經我們自己的伺服器
        public async Task<IActionResult> DownloadFile(string folder, string fileName)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager")
                return RedirectToAction("Index", "Login");

            if (folder != "逐字稿" && folder != "錄影錄音" && folder != "AI分析") return BadRequest();

            var safeFileName = Path.GetFileName(fileName); // 防止路徑跳脫
            var key = $"{folder}/{safeFileName}";

            if (!await _storage.ExistsAsync(key)) return NotFound();

            var url = _storage.GetPresignedUrl(key, TimeSpan.FromMinutes(15), downloadFileName: safeFileName);
            return Redirect(url);
        }

        // 🎯 直接查看檔案內容（不強制下載）
        //    逐字稿/AI分析：檔案小，直接由我們的伺服器讀出文字內容回傳（給前端 fetch() 用，同源不用處理 CORS）
        //    錄影錄音：302 導向 R2 簽名網址，讓 <video> 標籤直接播放（R2 原生支援 Range 請求，拖拉進度條沒問題）
        public async Task<IActionResult> ViewFile(string folder, string fileName)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager")
                return RedirectToAction("Index", "Login");

            if (folder != "逐字稿" && folder != "錄影錄音" && folder != "AI分析") return BadRequest();

            var safeFileName = Path.GetFileName(fileName);
            var key = $"{folder}/{safeFileName}";

            if (!await _storage.ExistsAsync(key)) return NotFound();

            if (folder == "錄影錄音")
            {
                var url = _storage.GetPresignedUrl(key, TimeSpan.FromMinutes(15));
                return Redirect(url);
            }

            try
            {
                var text = await _storage.DownloadTextAsync(key);
                return Content(text, "text/plain; charset=utf-8", System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "讀取雲端檔案失敗：" + ex.Message);
            }
        }

        // 🎯 依逐字稿內容產生 WebVTT 字幕檔，讓影片播放器的「CC 字幕」按鈕可以顯示逐字稿
        //    ⚠️ 時間軸是用逐字稿裡每行的時間戳，相對「第一句話」往後換算出來的估算值，
        //       不是跟錄影檔案逐格對齊（錄影是使用者按下畫面分享授權才真正開始錄，跟第一句話開口的時間可能有幾秒落差），
        //       僅供對照參考，不保證完全同步
        public async Task<IActionResult> TranscriptVtt(int roomId)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager")
                return RedirectToAction("Index", "Login");

            var room = await _context.Rooms.FirstOrDefaultAsync(r => r.Id == roomId);
            if (room == null || string.IsNullOrEmpty(room.TranscriptFileName))
                return Content("WEBVTT\n", "text/vtt", Encoding.UTF8);

            var key = $"逐字稿/{room.TranscriptFileName}";
            if (!await _storage.ExistsAsync(key))
                return Content("WEBVTT\n", "text/vtt", Encoding.UTF8);

            var content = await _storage.DownloadTextAsync(key);
            var lines = content.Split('\n');

            // 🎯 影片的 0 秒 = AI 面試官開始錄影的那一刻（上傳錄影時順手存了一個 .start 檔）。
            //    有這個時間才能讓字幕跟影片對齊；沒有（舊的場次）才退回「以第一句話當 0 秒」的估算。
            TimeSpan? recStart = null;
            if (!string.IsNullOrEmpty(room.RecordingFileName))
            {
                try
                {
                    var startKey = $"錄影錄音/{room.RecordingFileName}.start";
                    if (await _storage.ExistsAsync(startKey)
                        && DateTime.TryParse((await _storage.DownloadTextAsync(startKey)).Trim(), null,
                               System.Globalization.DateTimeStyles.RoundtripKind, out var rs))
                        recStart = rs.TimeOfDay;
                }
                catch { /* 讀不到就退回估算 */ }
            }
            var vtt = BuildVttFromTranscript(lines, recStart);
            return Content(vtt, "text/vtt", Encoding.UTF8);
        }

        // 🐛 這輪修正：逐字稿實際的時間格式是「[上午/下午HH:MM:SS]」或「[上午/下午 H:MM:SS]」
        //    （中文上午/下午前綴，時跟前綴之間有沒有空白、時是不是補零，兩種來源產生的格式不完全一樣：
        //    伺服器端音檔轉錄用 DateTime.ToString("tt h:mm:ss", zh-TW) 會帶空白、時不補零；
        //    瀏覽器端 SpeechRecognition 備援機制自己組字串則是不帶空白、時有補零），
        //    不是原本規則式假設的純西式「[HH:MM:SS]」24小時制格式——原本的規則式從頭到尾都比對不到任何一行，
        //    所以字幕永遠是空的，不是逐字稿內容或時間計算本身有問題，是這個規則式打從一開始就跟真正的格式對不上。
        private static readonly Regex TranscriptTimeRegex =
            new(@"^\[(上午|下午)\s*(\d{1,2}):(\d{2}):(\d{2})\]\s*(.+)$");

        // 逐字稿固定格式是「[上午/下午 H:MM:SS] 姓名：內容」，用第一句話的時間當基準點（t0 = 00:00.000）往後推算每句話的時間
        private string BuildVttFromTranscript(string[] lines, TimeSpan? recStart = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("WEBVTT");
            sb.AppendLine();

            var cues = new List<(TimeSpan time, string text)>();

            foreach (var raw in lines)
            {
                var line = raw?.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var m = TranscriptTimeRegex.Match(line);
                if (!m.Success) continue;

                var ampm = m.Groups[1].Value; // 上午 / 下午
                var hour = int.Parse(m.Groups[2].Value);
                var minute = int.Parse(m.Groups[3].Value);
                var second = int.Parse(m.Groups[4].Value);
                var text = m.Groups[5].Value;

                // 中文上午/下午轉成 24 小時制：上午12點是半夜0點，下午12點才是中午12點，其餘下午 +12
                int hour24 = ampm == "上午"
                    ? (hour == 12 ? 0 : hour)
                    : (hour == 12 ? 12 : hour + 12);

                var t = new TimeSpan(0, hour24, minute, second);
                cues.Add((t, text));
            }

            if (!cues.Any()) return sb.ToString();

            var t0 = recStart ?? cues[0].time;
            // 跨過午夜時（例如 23:59 → 00:01）時間會倒退，補一天
            TimeSpan Rel(TimeSpan x) { var d = x - t0; if (d < TimeSpan.FromHours(-12)) d += TimeSpan.FromHours(24); return d; }
            int index = 1;
            for (int i = 0; i < cues.Count; i++)
            {
                var start = Rel(cues[i].time);
                if (start < TimeSpan.Zero) start = TimeSpan.Zero;

                // 字幕顯示時間依字數決定（約 3~8 秒），如果下一句提早出現就提早結束，避免字幕重疊
                var end = start + TimeSpan.FromSeconds(Math.Min(8, Math.Max(3, cues[i].text.Length * 0.3)));
                if (i + 1 < cues.Count)
                {
                    var nextStart = Rel(cues[i + 1].time);
                    if (nextStart < end && nextStart > start) end = nextStart;
                }
                if (end <= start) end = start + TimeSpan.FromSeconds(1);

                sb.AppendLine(index.ToString());
                sb.AppendLine($"{FormatVttTime(start)} --> {FormatVttTime(end)}");
                sb.AppendLine(cues[i].text);
                sb.AppendLine();
                index++;
            }

            return sb.ToString();
        }

        private string FormatVttTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        }

        // 「面試會議_日期_職缺.副檔名」，職缺名稱裡不能當檔名的符號換成底線
        // 🎯 public static：MeetingHub 結束會議時，AI 面試官錄影上傳也要用同一套命名規則
        //    candidateName 有值時（AI 分析逐位存檔用），檔名會多一段求職者姓名後綴
        public static string BuildFileName(Room room, string ext, string? candidateName = null)
        {
            var jobTitle = room.Job?.Title ?? "未知職缺";
            foreach (var c in Path.GetInvalidFileNameChars())
                jobTitle = jobTitle.Replace(c, '_');
            jobTitle = jobTitle.Replace('/', '_').Replace('－', '_').Replace('-', '_');

            var dateText = (room.StartAt ?? room.ScheduledAt ?? DateTime.Now).ToString("yyyy-MM-dd");

            if (string.IsNullOrWhiteSpace(candidateName))
                return $"面試會議_{dateText}_{jobTitle}.{ext}";

            var safeName = candidateName;
            foreach (var c in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(c, '_');
            safeName = safeName.Replace('/', '_');

            return $"面試會議_{dateText}_{jobTitle}_{safeName}.{ext}";
        }
        //    求職者：session 存的是 Member.Id，要透過 Resume 反查
        //    員工（manager / hr）：session 存的是 Employee.Id，直接比對
        private async Task<RoomParticipant?> FindParticipantAsync(Room room, int sessionMemberId, string role)
        {
            if (role == "jobseeker")
            {
                return await _context.RoomParticipants
                    .Include(p => p.Resume)
                    .FirstOrDefaultAsync(p => p.RoomId == room.Id
                        && p.Role == ParticipantRole.Jobseeker
                        && p.Resume != null
                        && p.Resume.MembersId == sessionMemberId);
            }

            return await _context.RoomParticipants
                .FirstOrDefaultAsync(p => p.RoomId == room.Id && p.EmployeeId == sessionMemberId);
        }

        [HttpGet]
        public async Task<IActionResult> RoomStatus(string code)
        {
            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { found = false });

            return Json(new
            {
                found = true,
                canEnter = room.CanEnter(),
                statusText = room.StatusText(),
                startAt = room.StartAt?.ToString("yyyy-MM-ddTHH:mm:ss"),
                endAt = room.EndAt?.ToString("yyyy-MM-ddTHH:mm:ss")
            });
        }
    }
    public class AiNextQuestionRequest
    {
        public string? RoomCode { get; set; }
        public string? LastSpeakerName { get; set; }
        public string? LastSpeakerRole { get; set; }
        public bool LastWasQuestion { get; set; }
        public bool AiUnanswered { get; set; }   // AI 上次提問之後，沒有任何人出聲/發言
        public List<AiConvoLine>? Lines { get; set; }
        public bool Prefetch { get; set; }       // 預先準備題目（不是真的要開口），不受「會議裡剛有人聲」的限制
    }

    public class AiConvoLine
    {
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public string Text { get; set; } = "";
    }
}