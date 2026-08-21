using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

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

        // 🎯 逐字稿改用這個：不再依賴瀏覽器原生 SpeechRecognition（已證實會被 Jitsi 搶走麥克風獨佔權，
        //    導致大部分人只收到 no-speech，一整場話完全沒被記錄下來）。
        //    改成每個人自己把整場錄下來的麥克風音檔，直接送給 Gemini 做語音轉文字。
        //    回傳 null 代表失敗；回傳空字串或很短的內容，呼叫端可以視為「這段沒什麼有效內容」。
        //
        //    🐛 這輪修正：發現安靜/雜音佔多數的音檔，Gemini 有時不會乖乖回「（無語音內容）」，
        //       而是產生「同一句話重複幾十次」的幻覺輸出（例如整段都是「沒有看到」）。
        //       這裡除了強化 prompt 明確禁止這種行為，回傳前也會呼叫 CleanUpHallucination() 做防呆過濾。
        //
        //    🐛 這輪又修正：發現「逐字稿還是只收到主持人」這個問題還沒真的解決——
        //       根本原因不是轉錄邏輯本身，是「會議結束」廣播出去的當下，所有人（主持人+所有主管+所有求職者）
        //       幾乎同時觸發 submitMyAudioTranscript()，等於同時間有好幾個 SubmitAudioTranscript 請求
        //       一起打進來、一起呼叫 Gemini API，很容易一次撞到 Gemini 免費額度的每分鐘請求數限制（429），
        //       而這裡原本完全沒有重試機制，撞到就直接吃案回傳 null，剛好主持人自己那個請求（呼叫時機通常
        //       比其他人早一點點，因為是他觸發 EndMeeting 的）比較容易搶到額度，其他人就全部失敗。
        //       修正：(1) 加一個伺服器端的並發限制（同時最多 2 個轉錄請求打去 Gemini，其餘排隊等待，
        //       不要讓大家同時湧入直接把額度打爆）；(2) 收到 429 時做一次退避重試，不要直接放棄。
        private static readonly SemaphoreSlim _transcribeConcurrencyLimiter = new(2, 2);

        public async Task<string?> TranscribeAudioAsync(byte[] audioBytes, string mimeType)
        {
            var apiKey = _config["Gemini:ApiKey"]
                      ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                      ?? "";

            if (string.IsNullOrEmpty(apiKey)) return null;
            if (audioBytes.Length == 0) return "";

            // 🎯 排隊拿到「可以打 Gemini」的名額才繼續，避免會議結束當下所有人同時湧入
            await _transcribeConcurrencyLimiter.WaitAsync();
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(3); // 音訊檔案較大、轉錄需要一點時間，拉長逾時

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={apiKey}";
                var base64Audio = Convert.ToBase64String(audioBytes);

                var body = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new object[]
                            {
                                new { text =
                                    "請把這段音訊逐字轉成繁體中文文字稿，只要打字稿內容本身，不要加任何說明、不要加時間戳、不要加「逐字稿：」這種標題。\n" +
                                    "這段音訊大部分時間可能是安靜、環境雜音、呼吸聲、或視訊會議背景音等「非語言」聲音，這是正常情況，請只把「實際聽得出來的語音內容」打出來就好，" +
                                    "絕對不要為了填滿內容而重複輸出同一句話、同一個詞，也不要憑空腦補、猜測、或延伸沒有真的聽到的內容。如果只有零星幾句話，就只寫那幾句話，其餘什麼都不用寫。\n" +
                                    "如果整段音訊都沒有人講話（完全安靜無聲或只有雜音），就只回覆「（無語音內容）」這幾個字，不要輸出其他任何文字。" },
                                new { inline_data = new { mime_type = mimeType, data = base64Audio } }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        maxOutputTokens = 4000,
                        temperature = 0.2 // 🎯 降低溫度，減少「腦補/重複」這種幻覺行為的機率
                    }
                };

                var json = JsonSerializer.Serialize(body);

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var content = new StringContent(json, Encoding.UTF8, "application/json");
                        var response = await client.PostAsync(url, content);
                        var respBody = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            // 🎯 429 = 撞到速率限制，很可能等一下就好了，值得重試；其他錯誤（金鑰錯誤、額度整個用完等）
                            //    重試也沒用，直接放棄比較快
                            if ((int)response.StatusCode == 429 && attempt < 2)
                            {
                                Console.WriteLine($"[GeminiService] TranscribeAudioAsync 撞到 429 速率限制，{(attempt + 1) * 3} 秒後重試（第 {attempt + 1} 次）");
                                await Task.Delay((attempt + 1) * 3000);
                                continue;
                            }
                            Console.WriteLine($"[GeminiService] TranscribeAudioAsync 失敗：HTTP {(int)response.StatusCode}，內容前 300 字：{respBody.Substring(0, Math.Min(300, respBody.Length))}");
                            return null;
                        }

                        using var doc = JsonDocument.Parse(respBody);
                        var text = doc.RootElement
                            .GetProperty("candidates")[0]
                            .GetProperty("content")
                            .GetProperty("parts")[0]
                            .GetProperty("text")
                            .GetString() ?? "";

                        return CleanUpHallucination(text.Trim());
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GeminiService] TranscribeAudioAsync 例外（第 {attempt + 1} 次嘗試）：{ex.Message}");
                        if (attempt >= 2) return null;
                        await Task.Delay(2000);
                    }
                }

                return null;
            }
            finally
            {
                _transcribeConcurrencyLimiter.Release();
            }
        }

        // 🎯 防呆：把「同一行重複很多次」這種明顯的幻覺輸出擋下來
        //    - 連續重複的行先收斂成一行（例如「沒有看到」x50 → 只留一次）
        //    - 收斂完之後，如果整段內容大部分（>60%）都是被收斂掉的重複行，代表這整段轉錄結果很可能不可信，
        //      直接視為「沒有有效內容」（回傳「（無語音內容）」），不要把這種噪音存進逐字稿
        public static string CleanUpHallucination(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var rawLines = text.Replace("\r\n", "\n").Split('\n');
            var collapsed = new List<string>();
            int duplicateCount = 0;

            string? prev = null;
            foreach (var line in rawLines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                if (prev != null && trimmed == prev)
                {
                    duplicateCount++; // 跟上一行完全一樣，視為重複幻覺，不收進去
                    continue;
                }
                collapsed.Add(trimmed);
                prev = trimmed;
            }

            var totalLines = collapsed.Count + duplicateCount;
            if (totalLines > 0 && duplicateCount > 3 && duplicateCount >= totalLines * 0.6)
            {
                // 大部分內容都是重複幻覺，整段當作沒有有效內容
                return "（無語音內容）";
            }

            return string.Join(" ", collapsed); // 用空白接起來，避免存進逐字稿時產生沒有時間戳/講者前綴的裸行
        }

        // 🎯 AI 分析不能只靠逐字稿文字——逐字稿只有「說了什麼」，看不出語氣、表情。
        //    要做到真正的多模態分析（語氣/表情），得把錄影檔（畫面+聲音）直接餵給 Gemini，
        //    但錄影檔通常遠大於 inline_data 建議的上限（~20MB），所以改用 Gemini File API：
        //    先把整支影片上傳成一個「檔案資源」拿到 file_uri，之後每位求職者的分析都重複參照同一個 file_uri，
        //    不用每個人都重傳一次整支影片。回傳 null 代表上傳失敗，呼叫端要能 fallback 回純文字分析。
        public async Task<(string? fileUri, string? fileName)> UploadFileAsync(byte[] bytes, string mimeType, string displayName)
        {
            var apiKey = _config["Gemini:ApiKey"]
                      ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                      ?? "";
            if (string.IsNullOrEmpty(apiKey) || bytes.Length == 0) return (null, null);

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5); // 影片檔案較大，上傳 + Google 端處理都需要時間

            try
            {
                // Step 1：跟 Gemini 要一個「可續傳上傳網址」（resumable upload session）
                var startReq = new HttpRequestMessage(HttpMethod.Post,
                    $"https://generativelanguage.googleapis.com/upload/v1beta/files?key={apiKey}");
                startReq.Headers.Add("X-Goog-Upload-Protocol", "resumable");
                startReq.Headers.Add("X-Goog-Upload-Command", "start");
                startReq.Headers.Add("X-Goog-Upload-Header-Content-Length", bytes.Length.ToString());
                startReq.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
                startReq.Content = new StringContent(
                    JsonSerializer.Serialize(new { file = new { display_name = displayName } }),
                    Encoding.UTF8, "application/json");

                var startResp = await client.SendAsync(startReq);
                if (!startResp.IsSuccessStatusCode)
                {
                    var startErrBody = await startResp.Content.ReadAsStringAsync();
                    Console.WriteLine($"[GeminiService] UploadFileAsync 建立上傳工作階段失敗：HTTP {(int)startResp.StatusCode}，內容前 300 字：{startErrBody.Substring(0, Math.Min(300, startErrBody.Length))}");
                    return (null, null);
                }
                if (!startResp.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls))
                {
                    Console.WriteLine("[GeminiService] UploadFileAsync 失敗：回應裡沒有 X-Goog-Upload-URL 標頭");
                    return (null, null);
                }
                var uploadUrl = uploadUrls.FirstOrDefault();
                if (string.IsNullOrEmpty(uploadUrl))
                {
                    Console.WriteLine("[GeminiService] UploadFileAsync 失敗：X-Goog-Upload-URL 標頭是空的");
                    return (null, null);
                }

                // Step 2：把實際的影片位元組傳上去，一次傳完並 finalize
                var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
                uploadReq.Headers.Add("X-Goog-Upload-Offset", "0");
                uploadReq.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
                var byteContent = new ByteArrayContent(bytes);
                byteContent.Headers.ContentLength = bytes.Length;
                uploadReq.Content = byteContent;

                var uploadResp = await client.SendAsync(uploadReq);
                var uploadBody = await uploadResp.Content.ReadAsStringAsync();
                if (!uploadResp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[GeminiService] UploadFileAsync 上傳影片位元組失敗：HTTP {(int)uploadResp.StatusCode}，內容前 300 字：{uploadBody.Substring(0, Math.Min(300, uploadBody.Length))}");
                    return (null, null);
                }

                using var doc = JsonDocument.Parse(uploadBody);
                var fileEl = doc.RootElement.GetProperty("file");
                var fileName = fileEl.GetProperty("name").GetString(); // 例如 "files/abc123"，輪詢/刪除時要用
                var fileUri = fileEl.GetProperty("uri").GetString();   // generateContent 的 file_data 要用這個
                var state = fileEl.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : "ACTIVE";

                if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(fileUri))
                {
                    Console.WriteLine($"[GeminiService] UploadFileAsync 失敗：回應裡缺少 file.name 或 file.uri，原始內容前 300 字：{uploadBody.Substring(0, Math.Min(300, uploadBody.Length))}");
                    return (null, null);
                }

                // Step 3：影片檔要等 Google 端處理完（狀態變成 ACTIVE）才能拿去分析，短輪詢等它
                //    （音檔通常很快，影片檔可能要等數十秒，這裡最多等 1 分鐘）
                var attempts = 0;
                while (state == "PROCESSING" && attempts < 30)
                {
                    await Task.Delay(2000);
                    attempts++;
                    var pollResp = await client.GetAsync(
                        $"https://generativelanguage.googleapis.com/v1beta/{fileName}?key={apiKey}");
                    if (!pollResp.IsSuccessStatusCode) break;
                    var pollBody = await pollResp.Content.ReadAsStringAsync();
                    using var pollDoc = JsonDocument.Parse(pollBody);
                    state = pollDoc.RootElement.TryGetProperty("state", out var s2) ? s2.GetString() : "ACTIVE";
                }

                if (state != "ACTIVE")
                {
                    Console.WriteLine($"[GeminiService] UploadFileAsync 失敗：影片檔最終狀態是「{state}」，不是 ACTIVE（可能是處理失敗，或等了 1 分鐘還沒處理完）");
                    return (null, null); // 處理失敗或逾時，呼叫端要能 fallback 回純文字分析
                }

                return (fileUri, fileName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GeminiService] UploadFileAsync 發生例外：{ex.Message}");
                return (null, null);
            }
        }

        // 🎯 分析用完的暫存影片檔，主動刪掉比較乾淨（非必要——Google 那邊 48 小時後也會自動清掉）
        public async Task DeleteFileAsync(string fileName)
        {
            var apiKey = _config["Gemini:ApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(fileName)) return;
            try
            {
                var client = _httpClientFactory.CreateClient();
                await client.DeleteAsync($"https://generativelanguage.googleapis.com/v1beta/{fileName}?key={apiKey}");
            }
            catch
            {
                // 刪不掉就算了，反正 Google 端 48 小時後會自動清掉，不影響功能
            }
        }

        // 🎯 針對單一位求職者，用「錄影檔（多模態：畫面表情 + 聲音語氣）+ 逐字稿」一起分析，
        //    不再只看文字逐字稿——這樣才分析得出語氣、表情這些逐字稿完全看不出來的東西。
        //    fileUri 是 UploadFileAsync() 拿到的、已經在 Gemini 端 ACTIVE 的影片檔參照。
        public async Task<string?> AnalyzeInterviewVideoAsync(string fileUri, string fileMimeType, string candidateName, string transcript, int maxTokens)
        {
            var apiKey = _config["Gemini:ApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
            if (string.IsNullOrEmpty(apiKey)) return null;

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(3);

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={apiKey}";

            var promptText =
                $"這是一場面試的完整錄影（包含畫面與聲音），底下是逐字稿可以幫助你對照時間點與內容（可能包含多位求職者，發言前有標示姓名）：\n\n{transcript}\n\n" +
                $"請只針對「{candidateName}」這位求職者，實際觀看影片畫面、聆聽聲音語氣後進行分析（繁體中文），" +
                $"分析內容務必真的根據影片畫面與聲音判斷，不能只是把逐字稿文字複述一遍。請提供：\n" +
                $"1. 語氣表達（從聲音的語調、停頓、自信程度判斷）\n" +
                $"2. 面部表情與肢體語言（從畫面判斷是否緊張、專注、態度自然與否；如果全程畫面看不到這位求職者的臉，請誠實說明看不到、僅能依聲音判斷）\n" +
                $"3. 答題邏輯與內容品質\n" +
                $"4. AI 綜合評分（0-100 分，請用「AI 綜合評分：XX 分」這個格式單獨一行）\n" +
                $"5. 錄取建議\n" +
                $"6. 整體評語（完整寫完，不要中途省略）";

            var body = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = promptText },
                            new { file_data = new { mime_type = fileMimeType, file_uri = fileUri } }
                        }
                    }
                },
                generationConfig = new
                {
                    maxOutputTokens = maxTokens > 0 ? maxTokens : 3000,
                    temperature = 0.4
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
