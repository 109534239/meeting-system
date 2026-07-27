using Amazon.S3;
using Amazon.S3.Model;
using System.Text;

namespace InterviewProject.Services
{
    // 🎯 面試錄影/逐字稿/AI分析改存到 Cloudflare R2（S3 相容 API），不再存本機 wwwroot。
    //    這樣不管是本機執行還是部署在 Render，讀到的都是同一份雲端檔案，不會再有「本機有、網址沒有」的落差。
    //    R2 的三個必要設定值都是從 appsettings.json 的 "R2" 區塊或對應的環境變數讀進來：
    //      R2:AccountId         → Cloudflare 帳號的 Account ID（R2 API 端點會用到）
    //      R2:AccessKeyId       → R2 API Token 的 Access Key
    //      R2:SecretAccessKey   → R2 API Token 的 Secret Key
    //      R2:BucketName        → 要存檔案的 Bucket 名稱
    //    本機開發：填在 appsettings.Development.json 或 dotnet user-secrets
    //    Render 正式環境：填在 Environment Variables，key 用兩個底線寫巢狀設定，例如 R2__AccessKeyId
    public class R2StorageService
    {
        private readonly IAmazonS3? _client;
        private readonly string _bucket;
        private readonly bool _isConfigured;

        public R2StorageService(IConfiguration config)
        {
            var accountId = config["R2:AccountId"];
            var accessKey = config["R2:AccessKeyId"];
            var secretKey = config["R2:SecretAccessKey"];
            _bucket = config["R2:BucketName"] ?? "";

            _isConfigured = !string.IsNullOrWhiteSpace(accountId)
                         && !string.IsNullOrWhiteSpace(accessKey)
                         && !string.IsNullOrWhiteSpace(secretKey)
                         && !string.IsNullOrWhiteSpace(_bucket);

            if (_isConfigured)
            {
                var s3Config = new AmazonS3Config
                {
                    ServiceURL = $"https://{accountId}.r2.cloudflarestorage.com",
                    ForcePathStyle = true,
                    // R2 只支援 SigV4，且不需要對應「AWS 區域」的概念，這裡固定填 auto
                    AuthenticationRegion = "auto"
                };
                _client = new AmazonS3Client(accessKey, secretKey, s3Config);
            }
        }

        // 沒設定 R2 環境變數時，用這個檢查主動回報明確錯誤，不要讓程式默默存到別的地方造成混淆
        public void EnsureConfigured()
        {
            if (!_isConfigured || _client == null)
                throw new InvalidOperationException("尚未設定 Cloudflare R2（R2:AccountId / AccessKeyId / SecretAccessKey / BucketName），檔案無法儲存。");
        }

        public bool IsConfigured => _isConfigured;

        // key 的命名慣例跟原本 wwwroot 資料夾一致，例如 "逐字稿/面試會議_2026-07-26_助理專案經理.txt"
        public async Task UploadTextAsync(string key, string content)
        {
            EnsureConfigured();
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                ContentBody = content,
                ContentType = "text/plain; charset=utf-8"
            };
            await _client!.PutObjectAsync(request);
        }

        public async Task UploadStreamAsync(string key, Stream stream, string contentType)
        {
            EnsureConfigured();
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = stream,
                ContentType = contentType,
                AutoCloseStream = true
            };
            await _client!.PutObjectAsync(request);
        }

        public async Task<string> DownloadTextAsync(string key)
        {
            EnsureConfigured();
            using var resp = await _client!.GetObjectAsync(_bucket, key);
            using var reader = new StreamReader(resp.ResponseStream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        public async Task<bool> ExistsAsync(string key)
        {
            if (!_isConfigured || _client == null) return false;
            try
            {
                await _client.GetObjectMetadataAsync(_bucket, key);
                return true;
            }
            catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        // 影片/下載都直接用有時效的簽名網址讓瀏覽器直接跟 R2 拿資料，不繞經我們自己的伺服器
        // （省頻寬，而且 R2 原生支援 Range 請求，影片拖拉進度條不用額外處理）
        // downloadFileName 有值時，會強制瀏覽器跳出「另存新檔」；留空則是「就地顯示/播放」
        public string GetPresignedUrl(string key, TimeSpan validFor, string? downloadFileName = null)
        {
            EnsureConfigured();
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(validFor)
            };

            if (!string.IsNullOrEmpty(downloadFileName))
            {
                // 檔名有中文，單純 filename="..." 在部分瀏覽器會顯示亂碼，
                // 額外補上 RFC 5987 的 filename*=UTF-8''... 讓現代瀏覽器都能正確顯示中文檔名
                var encoded = Uri.EscapeDataString(downloadFileName);
                request.ResponseHeaderOverrides.ContentDisposition = $"attachment; filename=\"{encoded}\"; filename*=UTF-8''{encoded}";
            }

            return _client!.GetPreSignedURL(request);
        }

        // 列出某個「資料夾」前綴底下的所有檔案 key（R2 沒有真的資料夾，是用 key 前綴模擬）
        public async Task<List<string>> ListKeysAsync(string prefix)
        {
            if (!_isConfigured || _client == null) return new List<string>();

            var keys = new List<string>();
            string? continuationToken = null;
            do
            {
                var resp = await _client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    Prefix = prefix,
                    ContinuationToken = continuationToken
                });
                keys.AddRange(resp.S3Objects.Select(o => o.Key));
                continuationToken = resp.IsTruncated == true ? resp.NextContinuationToken : null;
            } while (continuationToken != null);

            return keys.OrderByDescending(k => k).ToList();
        }
    }
}
