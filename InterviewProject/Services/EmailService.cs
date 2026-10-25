using System.Net;
using System.Net.Mail;
using System.Text;

namespace InterviewProject.Services
{
    /// <summary>
    /// 寄 email 的共用服務，寄法跟 LoginController 的忘記密碼驗證碼信完全一樣
    /// （Gmail SMTP + 應用程式密碼）。寄信失敗只記錄、不丟例外，
    /// 避免寄信出問題時連帶讓排程／通知求職者這些主要動作也失敗。
    /// </summary>
    public class EmailService
    {
        private const string FromEmail = "angela296123@gmail.com";
        private const string AppPassword = "zgpj hcew cyqc qxiy";
        private const string CompanyName = "面試力達母";

        /// <returns>true = 寄出；false = 沒寄（地址不合法或寄送失敗）</returns>
        public async Task<bool> SendAsync(string? toEmail, string subject, string htmlBody)
        {
            if (string.IsNullOrWhiteSpace(toEmail) || !toEmail.Contains('@')) return false;

            try
            {
                using var smtpClient = new SmtpClient("smtp.gmail.com", 587);
                smtpClient.UseDefaultCredentials = false;
                smtpClient.Credentials = new NetworkCredential(FromEmail, AppPassword);
                smtpClient.EnableSsl = true;
                smtpClient.DeliveryFormat = SmtpDeliveryFormat.International;

                using var mail = new MailMessage();
                mail.From = new MailAddress(FromEmail, CompanyName, Encoding.UTF8);
                mail.To.Add(toEmail.Trim());
                mail.Subject = $"【{CompanyName}】{subject}";
                mail.SubjectEncoding = Encoding.UTF8;
                mail.BodyEncoding = Encoding.UTF8;
                mail.IsBodyHtml = true;
                mail.Body = htmlBody;

                await smtpClient.SendMailAsync(mail);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmailService] 寄信給 {toEmail} 失敗：{ex.Message}");
                return false;
            }
        }
    }
}
