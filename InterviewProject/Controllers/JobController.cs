using Microsoft.AspNetCore.Mvc;

namespace InterviewProject.Controllers
{
    public class JobController : Controller
    {
        public IActionResult Job_search()
        {
            return View();
        }
        public IActionResult Job_detail()
        {
            return View();
        }
        public IActionResult Faq()
        {
            return View();
        }
    }
}