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

        public AdminFAQController(AppDbContext db)
        {
            _db = db;
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
        // 權限：hr / manager / director
        //
        // 🎯 director（部門最高主管）與 hr/manager 的差異：
        //    1. 只看得到分派給「自己部門」的回報
        //    2. 不需要 FAQ 管理，不查詢 FAQ 清單
        //    3. 不需要「選擇部門」下拉選單，不查 DepartmentOptions
        // ==============================
        public async Task<IActionResult> Index(string visitorStatus = "pending", string visitorSort = "asc", string jobseekerStatus = "pending", string jobseekerSort = "asc")
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Home");

            bool isHr = role == "hr";
            bool isDirector = role == "director";

            ViewBag.MemberRole = role;
            ViewBag.MemberId = memberId;
            ViewBag.IsDirector = isDirector;

            // FAQ 管理：只有 HR 需要看到，其餘角色不查詢
            var faqs = isHr
                ? await _db.Faqs
                    .OrderBy(f => f.SortOrder)
                    .ThenByDescending(f => f.CreatedAt)
                    .ToListAsync()
                : new List<FAQ>();

            // director 只能看自己部門的回報，先查出自己的部門名稱
            string? myDepartment = null;
            if (isDirector)
            {
                var me = await _db.Employees.FindAsync(memberId.Value);
                myDepartment = me?.Department;
                ViewBag.MyDepartment = myDepartment;
            }

            // ── 訪客回覆 ──
            var visitorQuery = isDirector
                ? _db.FAQReports.Where(r => r.Role == "訪客" && r.Department == myDepartment)
                : _db.FAQReports.Where(r => r.Role == "訪客" && (r.Department == null || r.Department == "人力資源"));
            visitorQuery = ApplyStatusFilter(visitorQuery, visitorStatus);
            visitorQuery = ApplySort(visitorQuery, visitorSort);
            var visitorReports = await visitorQuery.ToListAsync();

            // ── 求職者回覆 ──
            var jobseekerQuery = isDirector
                ? _db.FAQReports.Where(r => r.Role == "求職者" && r.Department == myDepartment)
                : _db.FAQReports.Where(r => r.Role == "求職者" && (r.Department == null || r.Department == "人力資源"));
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
            if (isDirector)
            {
                ViewBag.VisitorPendingCount = await _db.FAQReports
                    .CountAsync(r => r.Role == "訪客" && r.Department == myDepartment && r.Status != "已回覆");
                ViewBag.JobseekerPendingCount = await _db.FAQReports
                    .CountAsync(r => r.Role == "求職者" && r.Department == myDepartment && r.Status != "已回覆");
            }
            else
            {
                ViewBag.VisitorPendingCount = await _db.FAQReports
                     .CountAsync(r => r.Role == "訪客" && (r.Department == null || r.Department == "人力資源") && r.Status != "已回覆");
                ViewBag.JobseekerPendingCount = await _db.FAQReports
                    .CountAsync(r => r.Role == "求職者" && (r.Department == null || r.Department == "人力資源") && r.Status != "已回覆");
            }

            // 部門下拉選單：director 不需要指派部門，不查詢；只有 hr/manager 需要
            // 來自 Employees 表中 role = "director" 的部門清單
            // ※ 請依實際 Employee 模型的類別/屬性名稱調整（Employees / Role / Department）
            ViewBag.DepartmentOptions = isDirector
                ? new List<string>()
                : await _db.Employees
                    .Where(e => e.Role == "director")
                    .Select(e => e.Department)
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Distinct()
                    .OrderBy(d => d)
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
        // 權限：hr / manager / director
        // 🎯 director 僅能查看分派給「自己部門」的回報，避免用網址猜 id 跨部門存取
        // ==============================
        public async Task<IActionResult> ReportDetail(int id)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            if (role != "hr" &&
                role != "manager" &&
                role != "director")
                return RedirectToAction("Index", "Home");

            var report = await _db.FAQReports
                .FirstOrDefaultAsync(x => x.Id == id);

            if (report == null)
                return NotFound();

            if (role == "director")
            {
                var me = await _db.Employees.FindAsync(memberId.Value);
                if (me == null || report.Department != me.Department)
                    return Forbid();
            }

            return View(report);
        }

        // ==============================
        // Q&A 回報：回覆問題
        // 權限：HR / Manager / Director
        // 🎯 director 僅能回覆分派給「自己部門」的回報，避免用網址猜 id 跨部門回覆
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

            if (role != "hr" &&
                role != "manager" &&
                role != "director")
                return RedirectToAction("Index", "Home");

            var report = await _db.FAQReports.FindAsync(id);

            if (report == null)
                return NotFound();

            if (role == "director")
            {
                var me = await _db.Employees.FindAsync(memberId.Value);
                if (me == null || report.Department != me.Department)
                    return Forbid();
            }

            if (string.IsNullOrWhiteSpace(replyContent))
            {
                TempData["Error"] = "請輸入回覆內容。";
                return RedirectToAction(nameof(ReportDetail), new { id });
            }

            report.ReplyContent = replyContent;
            report.Status = "已回覆";
            report.RepliedAt = DateTime.Now;

            // 🎯 只有尚未指派部門時才預設歸為人力資源；
            //    director 回覆時 Department 已經是自己的部門，不應被覆蓋掉
            if (string.IsNullOrWhiteSpace(report.Department))
            {
                report.Department = "人力資源";
            }

            await _db.SaveChangesAsync();

            string resultMessage = "Q&A 已成功回覆。";

            // 訪客回覆送出後，額外寄送 Email 通知（Email 取自 FAQReports.Email）
            if (report.Role == "訪客" && !string.IsNullOrWhiteSpace(report.Email))
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
        // Q&A 回報：列表上直接變更處理部門
        // 選擇下拉選單即直接更新，不需進入詳細頁
        // 權限：hr / manager（🎯 director 不開放指派部門，只能直接回覆自己部門的項目）
        // ==============================
        [HttpPost]
        [Route("AdminFAQ/AssignDepartment/{id}")]
        public async Task<IActionResult> AssignDepartment(int id, string department)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            if (memberId == null || (role != "hr" && role != "manager"))
                return Json(new { success = false, message = "權限不足" });

            var report = await _db.FAQReports.FindAsync(id);
            if (report == null)
                return Json(new { success = false, message = "找不到回報資料" });

            // 選擇「選擇部門」(空值) 時直接清空，不再視為錯誤
            report.Department = string.IsNullOrWhiteSpace(department) ? null : department;
            await _db.SaveChangesAsync();

            return Json(new { success = true, department = report.Department });
        }

        // ==============================
        // Email 通知：回覆 Q&A 後寄送給訪客
        // 寄信方式參考 LoginController 的 SendEmailAsync
        // ==============================
        private async Task SendReplyEmailAsync(string toEmail, string subject, string originalContent, string replyContent)
        {
            string fromEmail = "angela296123@gmail.com";
            string appPassword = "zgpj hcew cyqc qxiy";
            string companyName = "XXX公司";

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
                        <h3>您好：</h3>
                        <p>感謝您的來信，我們已回覆您所提出的問題。</p>
                        <p><b>您的提問內容：</b></p>
                        <p style='white-space: pre-wrap;'>{WebUtility.HtmlEncode(originalContent)}</p>
                        <p><b>我們的回覆：</b></p>
                        <p style='white-space: pre-wrap;'>{WebUtility.HtmlEncode(replyContent)}</p>
                        <br>
                        <p>如有其他問題歡迎再次與我們聯繫。</p>";

                    await smtpClient.SendMailAsync(mailMessage);
                }
            }
        }
    }
}