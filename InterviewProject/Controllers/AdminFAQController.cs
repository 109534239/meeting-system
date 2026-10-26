using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace InterviewProject.Controllers
{
    public class AdminFAQController : Controller
    {
        private readonly AppDbContext _db;
        private readonly InterviewProject.Services.EmailService _email;

        public AdminFAQController(AppDbContext db, InterviewProject.Services.EmailService email)
        {
            _db = db;
            _email = email;
        }

        // ==============================
        // 權限檢查：僅 hr
        // ==============================
        private bool IsHr()
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            return memberId != null && role == "hr";
        }

        // ==============================
        // 求職 Q&A 管理首頁
        // 包含：FAQ 管理 + Q&A 回報
        // 權限：hr / manager（職員）
        //
        // 🎯 manager（職員）與 hr 的差異：
        //    1. 只看得到 HR 指派給「自己」的回報
        //    2. 不需要 FAQ 管理，不查詢 FAQ 清單
        //    3. 不能指派職員，只能回覆指派給自己的回報
        // ==============================
        public async Task<IActionResult> Index(string visitorStatus = "pending", string visitorSort = "asc", string jobseekerStatus = "pending", string jobseekerSort = "asc")
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            if (role != "hr" && role != "manager")
                return RedirectToAction("Index", "Home");

            bool isHr = role == "hr";
            bool isManager = role == "manager";
            int myId = memberId.Value;
            // HR 自己回覆的回報會把 AssignedEmployeeId 寫成 HR 的 Id，仍算在 HR 這邊（不是指派給職員）
            var hrIds = await _db.Employees.Where(e => e.Role == "hr").Select(e => e.Id).ToListAsync();

            ViewBag.MemberRole = role;
            ViewBag.MemberId = memberId;

            // FAQ 管理：只有 HR 需要看到，其餘角色不查詢
            var faqs = isHr
                ? await _db.Faqs
                    .OrderBy(f => f.SortOrder)
                    .ThenByDescending(f => f.CreatedAt)
                    .ToListAsync()
                : new List<FAQ>();

            // ── 訪客回覆 ──
            var visitorQuery = isManager
                ? _db.FAQReports.Where(r => r.Role == "訪客" && r.AssignedEmployeeId == myId)
                : _db.FAQReports.Where(r => r.Role == "訪客" && (r.AssignedEmployeeId == null || hrIds.Contains(r.AssignedEmployeeId.Value)));
            visitorQuery = ApplyStatusFilter(visitorQuery, visitorStatus);
            visitorQuery = ApplySort(visitorQuery, visitorSort);
            var visitorReports = await visitorQuery.ToListAsync();

            // ── 求職者回覆 ──
            var jobseekerQuery = isManager
                ? _db.FAQReports.Where(r => r.Role == "求職者" && r.AssignedEmployeeId == myId)
                : _db.FAQReports.Where(r => r.Role == "求職者" && (r.AssignedEmployeeId == null || hrIds.Contains(r.AssignedEmployeeId.Value)));
            jobseekerQuery = ApplyStatusFilter(jobseekerQuery, jobseekerStatus);
            jobseekerQuery = ApplySort(jobseekerQuery, jobseekerSort);
            var jobseekerReports = await jobseekerQuery.ToListAsync();

            ViewBag.VisitorReports = visitorReports;
            ViewBag.JobseekerReports = jobseekerReports;

            ViewBag.VisitorStatus = visitorStatus;
            ViewBag.VisitorSort = visitorSort;
            ViewBag.JobseekerStatus = jobseekerStatus;
            ViewBag.JobseekerSort = jobseekerSort;

            // 待處理數量，不受篩選條件影響，用於分頁籤上的提示數字（同樣依角色套用不同過濾規則）
            if (isManager)
            {
                ViewBag.VisitorPendingCount = await _db.FAQReports
                    .CountAsync(r => r.Role == "訪客" && r.AssignedEmployeeId == myId && r.Status != "已回覆");
                ViewBag.JobseekerPendingCount = await _db.FAQReports
                    .CountAsync(r => r.Role == "求職者" && r.AssignedEmployeeId == myId && r.Status != "已回覆");
            }
            else
            {
                ViewBag.VisitorPendingCount = await _db.FAQReports
                     .CountAsync(r => r.Role == "訪客" && (r.AssignedEmployeeId == null || hrIds.Contains(r.AssignedEmployeeId.Value)) && r.Status != "已回覆");
                ViewBag.JobseekerPendingCount = await _db.FAQReports
                    .CountAsync(r => r.Role == "求職者" && (r.AssignedEmployeeId == null || hrIds.Contains(r.AssignedEmployeeId.Value)) && r.Status != "已回覆");
            }

            // 職員下拉選單（名字（部門））：只有 hr 需要；職員一律是 Role = "manager"
            ViewBag.StaffOptions = isManager
                ? new List<Employee>()
                : await _db.Employees
                    .Where(e => e.Role == "manager")
                    .OrderBy(e => e.Department).ThenBy(e => e.Name)
                    .ToListAsync();

            return View(faqs);
        }

        // ==============================
        // 狀態篩選：待處理(預設) / 已回覆 / 全部
        // ==============================
        private static IQueryable<FAQReport> ApplyStatusFilter(IQueryable<FAQReport> query, string status)
        {
            return status switch
            {
                "replied" => query.Where(r => r.Status == "已回覆"),
                "all" => query,
                _ => query.Where(r => r.Status != "已回覆") // 預設：待處理
            };
        }

        // ==============================
        // 時間排序：asc(預設，越早在上面) / desc
        // ==============================
        private static IQueryable<FAQReport> ApplySort(IQueryable<FAQReport> query, string sort)
        {
            return sort == "desc"
                ? query.OrderByDescending(r => r.CreatedAt)
                : query.OrderBy(r => r.CreatedAt);
        }

        // ==============================
        // 新增 FAQ 頁面
        // 權限：僅 hr
        // ==============================
        public IActionResult Create()
        {
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            return View();
        }

        // ==============================
        // 新增 FAQ
        // 權限：僅 hr
        // ==============================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(FAQ faq)
        {
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            faq.CreatedAt = DateTime.Now;

            _db.Faqs.Add(faq);
            await _db.SaveChangesAsync();

            TempData["Success"] = "FAQ 已新增";
            return RedirectToAction("Index");
        }

        // ==============================
        // 編輯 FAQ 頁面
        // 權限：僅 hr
        // ==============================
        public async Task<IActionResult> Edit(int id)
        {
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            var faq = await _db.Faqs.FindAsync(id);
            if (faq == null) return NotFound();

            return View(faq);
        }

        // ==============================
        // 儲存編輯 FAQ
        // 權限：僅 hr
        // ==============================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(FAQ faq)
        {
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            var existing = await _db.Faqs.FindAsync(faq.Id);
            if (existing == null) return NotFound();

            existing.Question = faq.Question;
            existing.Answer = faq.Answer;
            existing.SortOrder = faq.SortOrder;
            existing.IsActive = faq.IsActive;

            await _db.SaveChangesAsync();

            TempData["Success"] = "FAQ 已更新";
            return RedirectToAction("Index");
        }

        // ==============================
        // 刪除 FAQ
        // 權限：僅 hr
        // ==============================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            var faq = await _db.Faqs.FindAsync(id);
            if (faq == null) return NotFound();

            _db.Faqs.Remove(faq);
            await _db.SaveChangesAsync();

            TempData["Success"] = "FAQ 已刪除";
            return RedirectToAction("Index");
        }

        // ==============================
        // FAQ 上架 / 下架切換
        // 權限：僅 hr
        // ==============================
        [HttpPost]
        [Route("AdminFAQ/ToggleActive/{id}")]
        public async Task<IActionResult> ToggleActive(int id)
        {
            if (!IsHr())
                return Json(new { success = false, message = "權限不足" });

            var faq = await _db.Faqs.FindAsync(id);
            if (faq == null)
                return Json(new { success = false, message = "找不到 FAQ" });

            faq.IsActive = !faq.IsActive;
            await _db.SaveChangesAsync();

            return Json(new
            {
                success = true,
                isActive = faq.IsActive
            });
        }

        // ==============================
        // Q&A 回報詳細資料
        // 權限：hr / manager
        // 🎯 manager 僅能查看指派給「自己」的回報，避免用網址猜 id 看到別人的
        // ==============================
        public async Task<IActionResult> ReportDetail(int id)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            if (role != "hr" && role != "manager")
                return RedirectToAction("Index", "Home");

            var report = await _db.FAQReports
                .FirstOrDefaultAsync(x => x.Id == id);

            if (report == null)
                return NotFound();

            if (role == "manager" && report.AssignedEmployeeId != memberId.Value)
                return Forbid();

            return View(report);
        }

        // ==============================
        // Q&A 回報：回覆問題
        // 權限：HR / Manager
        // 🎯 manager 僅能回覆指派給「自己」的回報，避免用網址猜 id 回覆別人的
        // ==============================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReplyReport(
            int id,
            string replyContent)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            if (role != "hr" && role != "manager")
                return RedirectToAction("Index", "Home");

            var report = await _db.FAQReports.FindAsync(id);

            if (report == null)
                return NotFound();

            if (role == "manager" && report.AssignedEmployeeId != memberId.Value)
                return Forbid();

            if (string.IsNullOrWhiteSpace(replyContent))
            {
                TempData["Error"] = "請輸入回覆內容。";
                return RedirectToAction(nameof(ReportDetail), new { id });
            }

            report.ReplyContent = replyContent;
            report.Status = "已回覆";
            report.RepliedAt = DateTime.Now;

            // 🎯 只有尚未指派時才預設歸為人力資源；
            //    指派的職員回覆時 Department 已經是他的部門，不應被覆蓋掉
            if (string.IsNullOrWhiteSpace(report.Department))
            {
                report.Department = "人力資源";
            }

            // 🎯 HR 自己回覆（還沒指派給職員）時，AssignedEmployeeId 寫成這位 HR 的 Id
            if (role == "hr" && report.AssignedEmployeeId == null)
            {
                report.AssignedEmployeeId = memberId.Value;
            }

            await _db.SaveChangesAsync();

            string resultMessage = "Q&A 已成功回覆。";

            // 回覆送出後寄 Email 給提問者（訪客、求職者都寄，內容含回覆內容；Email 取自 FAQReports.Email，
            // 求職者的 Email 是送出時從會員資料帶入的）。每次送出只寄一封。
            if (!string.IsNullOrWhiteSpace(report.Email))
            {
                try
                {
                    await SendReplyEmailAsync(report.Email, report.Subject, report.Content, replyContent);
                }
                catch (Exception ex)
                {
                    string errorMsg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    resultMessage += $"（但 Email 通知寄送失敗：{errorMsg}）";
                }
            }

            TempData["Success"] = resultMessage;

            // 送出後直接返回該身份（訪客／求職者）的預設列表
            string backTab = report.Role == "求職者" ? "jobseeker" : "visitor";
            return RedirectToAction(nameof(Index), new { tab = backTab });
        }

        // ==============================
        // Q&A 回報：列表上直接指派處理職員
        // 選擇下拉選單（名字（部門））即直接更新，並寄 email 通知被指派的職員
        // 權限：僅 hr
        // ==============================
        [HttpPost]
        [Route("AdminFAQ/AssignStaff/{id}")]
        public async Task<IActionResult> AssignStaff(int id, int? employeeId)
        {
            if (!IsHr())
                return Json(new { success = false, message = "權限不足" });

            var report = await _db.FAQReports.FindAsync(id);
            if (report == null)
                return Json(new { success = false, message = "找不到回報資料" });

            // 選擇「選擇職員」(空值) 時清空，回到由 HR 自己處理
            if (employeeId == null)
            {
                report.AssignedEmployeeId = null;
                report.Department = null;
                await _db.SaveChangesAsync();
                return Json(new { success = true });
            }

            var staff = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId.Value && e.Role == "manager");
            if (staff == null)
                return Json(new { success = false, message = "找不到這位職員" });

            bool changed = report.AssignedEmployeeId != staff.Id;
            report.AssignedEmployeeId = staff.Id;
            report.Department = staff.Department;
            await _db.SaveChangesAsync();

            // 只有「換了人」才寄信，重複選同一位不會再寄
            bool emailFailed = false;
            if (changed)
            {
                var link = $"{Request.Scheme}://{Request.Host}/AdminFAQ/ReportDetail/{report.Id}";
                var body = $@"
                    <h3>{WebUtility.HtmlEncode(staff.Name)} 您好：</h3>
                    <p>人資已將一則 Q&amp;A 回報（{WebUtility.HtmlEncode(report.Role)}）指派給您處理。</p>
                    <p><b>主旨：</b>{WebUtility.HtmlEncode(report.Subject)}<br>
                       <b>提問者：</b>{WebUtility.HtmlEncode(report.Name)}</p>
                    <div style='white-space:pre-wrap;line-height:1.8;'>{WebUtility.HtmlEncode(report.Content).Trim()}</div>
                    <p>回覆連結（請先登入系統）：<a href='{link}'>{link}</a></p>
                    <br><p>此信件由系統自動寄出，請勿直接回覆。</p>";
                var toEmail = !string.IsNullOrWhiteSpace(staff.Account) && staff.Account.Contains('@') ? staff.Account.Trim() : null;
                emailFailed = !await _email.SendAsync(toEmail, "有一則 Q&A 回報指派給您", body);
            }

            return Json(new { success = true, emailFailed });
        }

        // ==============================
        // Email 通知：回覆 Q&A 後寄送給訪客
        // 寄信方式參考 LoginController 的 SendEmailAsync
        // ==============================
        private async Task SendReplyEmailAsync(string toEmail, string subject, string originalContent, string replyContent)
        {
            string fromEmail = "angela296123@gmail.com";
            string appPassword = "zgpj hcew cyqc qxiy";
            string companyName = "面試力達母";

            using (var smtpClient = new SmtpClient("smtp.gmail.com", 587))
            {
                smtpClient.UseDefaultCredentials = false;
                smtpClient.Credentials = new NetworkCredential(fromEmail, appPassword);
                smtpClient.EnableSsl = true;
                smtpClient.DeliveryFormat = SmtpDeliveryFormat.International;

                using (var mailMessage = new MailMessage())
                {
                    mailMessage.From = new MailAddress(fromEmail, companyName, Encoding.UTF8);
                    mailMessage.To.Add(toEmail);
                    mailMessage.Subject = $"【{companyName}】您的Q&A提問已回覆：{subject}";
                    mailMessage.SubjectEncoding = Encoding.UTF8;
                    mailMessage.BodyEncoding = Encoding.UTF8;
                    mailMessage.IsBodyHtml = true;

                    mailMessage.Body = $@"
                        <p>您好：</p>

                        <p>感謝您透過 {companyName} Q&amp;A 系統提出問題。</p>

                        <p>您所提出的問題已由相關單位完成回覆，內容如下：</p>

                        <hr>

                        <p><strong>提問主旨</strong></p>
                        <p>{WebUtility.HtmlEncode(subject)}</p>

                        <br>

                        <p><strong>{companyName} 回覆</strong></p>

                        <div style='white-space: pre-wrap; line-height:1.8;'>{WebUtility.HtmlEncode(replyContent).Trim()}</div>

                        <p>若您仍有其他疑問，歡迎再次透過 {companyName} Q&amp;A 系統與我們聯繫。</p>

                        <hr>

                        <br>

                        <p><strong>您的提問內容</strong></p>

                        <div style='white-space: pre-wrap; line-height:1.8;'>{WebUtility.HtmlEncode(originalContent).Trim()}</div>

                        <hr>

                        <br>

                        <p style='color:#888;font-size:12px;'>
                        ※ 此信件由系統自動寄送，請勿直接回覆此郵件。
                        </p>";


                    await smtpClient.SendMailAsync(mailMessage);
                }
            }
        }
    }
}