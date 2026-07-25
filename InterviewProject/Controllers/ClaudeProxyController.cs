using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace InterviewProject.Controllers
{
    /// <summary>
    /// 後端 Proxy：前端呼叫 /Claude/Ask，由後端轉發到 Gemini API
    /// 解決 CORS 問題，使用 Google AI Studio 的免費 Gemini API Key
    /// </summary>
    [Route("Claude")]
    public class ClaudeProxyController : Controller
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;

        public ClaudeProxyController(IHttpClientFactory httpClientFactory, IConfiguration config)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
        }

        // POST /Claude/Ask
        [HttpPost("Ask")]
        public async Task<IActionResult> Ask([FromBody] ClaudeAskRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Prompt))
                return BadRequest(new { error = "Prompt 不能為空" });

            // API Key 從 appsettings.json 或環境變數取得
            var apiKey = _config["Gemini:ApiKey"]
                      ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                      ?? "";

            if (string.IsNullOrEmpty(apiKey))
                return StatusCode(500, new { error = "未設定 Gemini API Key" });

            var client = _httpClientFactory.CreateClient();

            // 🎯 修正：gemini-1.5-flash 已經被 Google 停用（呼叫一律回傳 404），跟你的 API Key 對不對無關，
            //    換成目前還在服務的模型。之後如果 Google 又停用新的模型，這裡的名稱要再更新
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={apiKey}";

            // 把 system prompt 合併進 user message（Gemini 免費版不支援獨立 system role）
            var systemPart = request.System ?? "你是台灣企業面試主管王大明。說繁體中文，語氣專業親切，像真人一樣思考。";
            var fullPrompt = $"{systemPart}\n\n{request.Prompt}";

            var body = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[] { new { text = fullPrompt } }
                    }
                },
                generationConfig = new
                {
                    maxOutputTokens = request.MaxTokens > 0 ? request.MaxTokens : 200,
                    temperature = 0.8
                }
            };

            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content);
            var respBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, new { error = respBody });

            using var doc = JsonDocument.Parse(respBody);
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? "";

            return Ok(new { text = text.Trim() });
        }
    }

    public class ClaudeAskRequest
    {
        public string? Prompt { get; set; }
        public string? System { get; set; }
        public int MaxTokens { get; set; } = 200;
    }
}
