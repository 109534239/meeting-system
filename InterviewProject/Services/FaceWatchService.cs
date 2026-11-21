using System.Collections.Concurrent;
using System.Text.Json;
using InterviewProject.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace InterviewProject.Services
{
    // 🎯 臉部觀察：會議進行中，每隔一段時間擷取每位求職者的視訊畫面，交給 Gemini 判讀
    //    1. 口罩提醒：連續 2 次判讀為戴口罩，就廣播「MaskAlert」，全場頁面上方橫幅顯示「求職者-xxx 請不要戴口罩」
    //    2. 會後 AI 分析：把取樣結果整理成「臉部觀察」段落附在每位求職者的報告後面（僅供參考，不計入評分）
    public class FaceWatchService : BackgroundService
    {
        public const int IntervalSec = 10;

        public class FaceSample { public DateTime At; public string Label = ""; public bool Face; public bool Mask; public string Expr = "看不清"; public string Gaze = "看不清"; }

        private static readonly ConcurrentDictionary<string, List<FaceSample>> _samples = new();
        private static readonly ConcurrentDictionary<string, DateTime> _startedAt = new();
        private static readonly ConcurrentDictionary<string, Dictionary<string, int>> _maskStreak = new();
        private static readonly ConcurrentDictionary<string, string> _lastAlert = new();

        private readonly JitsiBotService _bot;
        private readonly IServiceScopeFactory _scopes;
        private readonly IHubContext<MeetingHub> _hub;

        public FaceWatchService(JitsiBotService bot, IServiceScopeFactory scopes, IHubContext<MeetingHub> hub)
        {
            _bot = bot; _scopes = scopes; _hub = hub;
        }

        private readonly Dictionary<string, DateTime> _lastCycle = new();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 每 3 秒巡一次：新開始的會議 AI 一進場就先偵測一次（會議最開始就檢查），之後全程每 IntervalSec 秒一輪
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch { break; }
                try
                {
                    var codes = _bot.ActiveRoomCodes();
                    foreach (var gone in _maskStreak.Keys.Where(k => !codes.Contains(k)).ToList())
                    {
                        _maskStreak.TryRemove(gone, out _); _lastAlert.TryRemove(gone, out _); _lastCycle.Remove(gone);
                    }
                    foreach (var code in codes)
                    {
                        if (_lastCycle.TryGetValue(code, out var last) && (DateTime.UtcNow - last).TotalSeconds < IntervalSec) continue;
                        _lastCycle[code] = DateTime.UtcNow;
                        await CycleAsync(code, stoppingToken);
                    }
                }
                catch (Exception ex) { Console.WriteLine("[FaceWatch] 例外：" + ex.Message); }
            }
        }

        private async Task CycleAsync(string code, CancellationToken ct)
        {
            var frames = await _bot.CaptureFacesAsync(code);
            if (frames.Count == 0) return;
            _startedAt.TryAdd(code, DateTime.UtcNow);
            using var scope = _scopes.CreateScope();
            var gemini = scope.ServiceProvider.GetRequiredService<GeminiService>();
            var now = DateTime.UtcNow;
            var results = await Task.WhenAll(frames.Select(async f =>
            {
                var txt = await gemini.AnalyzeFaceAsync(f.Jpeg);
                return (f.Label, Sample: Parse(txt, f.Label, now));
            }));
            var streak = _maskStreak.GetOrAdd(code, _ => new Dictionary<string, int>());
            var list = _samples.GetOrAdd(code, _ => new List<FaceSample>());
            lock (list)
            {
                foreach (var r in results)
                {
                    if (r.Sample == null) continue;
                    if (list.Count < 3000) list.Add(r.Sample);
                    streak.TryGetValue(r.Label, out var n);
                    if (r.Sample.Mask) n++;
                    else if (r.Sample.Face) n = 0; // 看得到臉而且沒戴口罩才解除；看不到臉就維持原狀
                    streak[r.Label] = n;
                }
            }
            List<string> alert;
            lock (list) alert = streak.Where(kv => kv.Value >= 2).Select(kv => kv.Key).OrderBy(x => x).ToList();
            var key = string.Join("|", alert);
            _lastAlert.TryGetValue(code, out var prev);
            if (key != (prev ?? "") || alert.Count > 0)
            {
                _lastAlert[code] = key;
                try { await _hub.Clients.Group(code).SendAsync("MaskAlert", alert.ToArray(), ct); } catch { }
            }
        }

        private static FaceSample? Parse(string? json, string label, DateTime at)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var s = json.Trim();
                var a = s.IndexOf('{'); var b = s.LastIndexOf('}');
                if (a < 0 || b <= a) return null;
                using var doc = JsonDocument.Parse(s.Substring(a, b - a + 1));
                var r = doc.RootElement;
                bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
                string S(string k, string d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? d) : d;
                var face = B("face");
                var expr = S("expression", "看不清"); if (expr != "微笑" && expr != "平靜" && expr != "皺眉") expr = "看不清";
                var gaze = S("gaze", "看不清"); if (gaze != "看向鏡頭" && gaze != "偏離鏡頭") gaze = "看不清";
                return new FaceSample { At = at, Label = label, Face = face, Mask = face && B("mask"), Expr = face ? expr : "看不清", Gaze = face ? gaze : "看不清" };
            }
            catch { return null; }
        }

        public static void Clear(string code)
        {
            _samples.TryRemove(code, out _); _startedAt.TryRemove(code, out _); _maskStreak.TryRemove(code, out _); _lastAlert.TryRemove(code, out _);
        }

        // 會後附在每位求職者 AI 分析報告後面的「臉部觀察」段落（不計入評分）
        public static string BuildReport(string code, string candidateName)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("════ 六、臉部觀察（僅供參考，不計入評分）════");
            List<FaceSample> mine = new();
            if (_samples.TryGetValue(code, out var all))
                lock (all) mine = all.Where(s => s.Label == "求職者-" + candidateName || s.Label.EndsWith(candidateName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0)
            {
                sb.AppendLine($"本場沒有取得「{candidateName}」的視訊畫面（可能全程未開鏡頭），無法提供臉部觀察。");
                return sb.ToString().TrimEnd();
            }
            int n = mine.Count, faceN = mine.Count(s => s.Face), maskN = mine.Count(s => s.Mask);
            var withFace = mine.Where(s => s.Face).ToList();
            string Cnt(IEnumerable<FaceSample> src, Func<FaceSample, string> f, string[] keys) => string.Join("、", keys.Select(k => $"{k} {src.Count(s => f(s) == k)} 次"));
            var started = _startedAt.TryGetValue(code, out var st) ? st : mine.Min(s => s.At);
            sb.AppendLine($"取樣方式：會議中約每 {IntervalSec} 秒擷取一次該求職者的視訊畫面，由 AI 判讀，共 {n} 次，其中 {faceN} 次看得到清楚的臉。");
            if (maskN == 0) sb.AppendLine("口罩：全程未偵測到配戴口罩。");
            else
            {
                var mins = mine.Where(s => s.Mask).Select(s => (int)Math.Floor((s.At - started).TotalMinutes) + 1).Distinct().OrderBy(x => x).Take(8).ToList();
                sb.AppendLine($"口罩：{maskN} 次判讀為配戴口罩（佔看得到臉次數的 {(faceN > 0 ? maskN * 100 / faceN : 0)}%），約在會議第 {string.Join("、", mins)} 分鐘。");
            }
            if (faceN > 0)
            {
                sb.AppendLine("表情分布：" + Cnt(withFace, s => s.Expr, new[] { "微笑", "平靜", "皺眉", "看不清" }));
                sb.AppendLine("視線：" + Cnt(withFace, s => s.Gaze, new[] { "看向鏡頭", "偏離鏡頭", "看不清" }));
            }
            sb.AppendLine("說明：以上只是畫面的粗略次數統計，不代表情緒、個性或能力，沒有計入上方任何分數；視線偏離可能只是在看螢幕上的其他人或筆記，戴口罩也可能有正當原因。請人工對照影片再判斷。");
            return sb.ToString().TrimEnd();
        }
    }
}
