using Microsoft.AspNetCore.Mvc;
using InterviewProject.Services;

namespace InterviewProject.Controllers
{
    /// <summary>
    /// 後端 Proxy：前端呼叫 /Claude/Ask，由後端轉發到 Gemini API
    /// 解決 CORS 問題，使用 Google AI Studio 的免費 Gemini API Key
    /// 🎯 實際呼叫邏輯已經搬到 GeminiService，這裡只是包一層 HTTP 端點給前端用
    /// </summary>
    [Route("Claude")]
    public class ClaudeProxyController : Controller
    {
        private readonly GeminiService _gemini;

        public ClaudeProxyController(GeminiService gemini)
        {
            _gemini = gemini;
        }

        // POST /Claude/Ask
        [HttpPost("Ask")]
        public async Task<IActionResult> Ask([FromBody] ClaudeAskRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Prompt))
                return BadRequest(new { error = "Prompt 不能為空" });

            var text = await _gemini.AskAsync(request.Prompt, request.System, request.MaxTokens);

            if (text == null)
                return StatusCode(500, new { error = "AI 分析失敗（Gemini:ApiKey 是否正確、或 API 額度用完）" });

            return Ok(new { text });
        }
    }

    public class ClaudeAskRequest
    {
        public string? Prompt { get; set; }
        public string? System { get; set; }
        public int MaxTokens { get; set; } = 200;
    }
}
