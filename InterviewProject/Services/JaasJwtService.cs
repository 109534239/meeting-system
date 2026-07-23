using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace InterviewProject.Services
{
    /// <summary>
    /// 簽發 JaaS (8x8.vc) 要求的 JWT。沒有這個，JaaS 會直接拒絕所有人加入（"This call requires authentication"）。
    /// 私鑰不會進版本控制，從專案根目錄的 PEM 檔讀取，路徑由 appsettings.json 的 JaaS:PrivateKeyPath 指定。
    /// </summary>
    public class JaasJwtService
    {
        private readonly string _appId;
        private readonly string _keyId;
        private readonly RSA _privateKey;

        public JaasJwtService(IConfiguration config, IWebHostEnvironment env)
        {
            _appId = config["JaaS:AppId"]
                ?? throw new InvalidOperationException("appsettings.json 缺少 JaaS:AppId 設定");
            _keyId = config["JaaS:KeyId"]
                ?? throw new InvalidOperationException("appsettings.json 缺少 JaaS:KeyId 設定");

            var pemFileName = config["JaaS:PrivateKeyPath"] ?? "jaas-private-key.pem";
            var pemPath = Path.Combine(env.ContentRootPath, pemFileName);

            if (!File.Exists(pemPath))
                throw new FileNotFoundException(
                    $"找不到 JaaS 私鑰檔案：{pemPath}。請把從 jaas.8x8.vc 下載的 .pem 私鑰檔放到專案根目錄，檔名要跟 appsettings.json 的 JaaS:PrivateKeyPath 一致。");

            _privateKey = RSA.Create();
            _privateKey.ImportFromPem(File.ReadAllText(pemPath));
        }

        /// <summary>
        /// 產生一份「進場憑證」JWT。
        /// </summary>
        /// <param name="roomCode">房間代碼（不含 AppID 前綴，跟 Room.JitsiRoomName 一致）</param>
        /// <param name="userId">目前這個人在我們系統裡的 Id（求職者用 Member.Id，員工用 Employee.Id，AI 面試官用固定字串）</param>
        /// <param name="displayName">要顯示的名字，例如「最高主管：張小恩」</param>
        /// <param name="isModerator">是不是主持人。只有「這場會議受邀的 director」才應該是 true</param>
        public string GenerateToken(string roomCode, string userId, string displayName, bool isModerator)
        {
            var signingCredentials = new SigningCredentials(
                new RsaSecurityKey(_privateKey), SecurityAlgorithms.RsaSha256)
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
            };

            var header = new JwtHeader(signingCredentials)
            {
                ["kid"] = _keyId,
                ["typ"] = "JWT"
            };

            var now = DateTimeOffset.UtcNow;

            var payload = new JwtPayload
            {
                { "iss", "chat" },
                { "aud", "jitsi" },
                { "sub", _appId },
                { "room", roomCode }, // 鎖定只能進這一個房間，不是萬用 "*"
                { "exp", now.AddHours(8).ToUnixTimeSeconds() }, // 憑證本身的效期，跟「會議時長」是兩回事，會議不受這個限制
                { "nbf", now.AddSeconds(-10).ToUnixTimeSeconds() },
                {
                    "context", new Dictionary<string, object>
                    {
                        {
                            "user", new Dictionary<string, object>
                            {
                                { "id", userId },
                                { "name", displayName },
                                { "moderator", isModerator ? "true" : "false" }
                            }
                        }
                    }
                }
            };

            var token = new JwtSecurityToken(header, payload);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
