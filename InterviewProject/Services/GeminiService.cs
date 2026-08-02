using System.Text;
using System.Text.Json;

namespace InterviewProject.Services
{
    // 🎯 把呼叫 Gemini API 的邏輯獨立出來，讓 ClaudeProxyController（前端 /Claude/Ask 用）
    //    跟 RoomController（伺服器端產生逐位求職者的 AI 分析報告用）可以共用同一份邏輯，不用重複寫兩次
    public class GeminiService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;

        public GeminiService(IHttpClientFactory httpClientFactory, IConfiguration config)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
        }

        // 回傳 null 代表失敗（金鑰沒設定、API 出錯等），呼叫端自己判斷要怎麼處理
        public async Task<string?> AskAsync(string prompt, string? system, int maxTokens)
        {
            var apiKey = _config["Gemini:ApiKey"]
                      ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                      ?? "";

            if (string.IsNullOrEmpty(apiKey)) return null;

            var client = _httpClientFactory.CreateClient();

            // 🎯 gemini-1.5-flash 已停用，改用還在服務的模型；之後如果 Google 又停用要再換
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={apiKey}";

            var systemPart = system ?? "你是台灣企業面試主管王大明。說繁體中文，語氣專業親切，像真人一樣思考。";
            var fullPrompt = $"{systemPart}\n\n{prompt}";

            var body = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = fullPrompt } } }
                },
                generationConfig = new
                {
                    maxOutputTokens = maxTokens > 0 ? maxTokens : 200,
                    temperature = 0.8
                }
            };

            try
            {
                var json = JsonSerializer.Serialize(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);
                var respBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(respBody);
                var text = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString() ?? "";

                return text.Trim();
            }
            catch
            {
                return null;
            }
        }
    }
}
