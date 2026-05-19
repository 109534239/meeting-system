using Microsoft.AspNetCore.Mvc;

namespace InterviewProject.Controllers
{
    public class FAQController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}