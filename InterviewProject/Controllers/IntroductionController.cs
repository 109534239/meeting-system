using Microsoft.AspNetCore.Mvc;

namespace InterviewProject.Controllers
{
    public class IntroductionController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}