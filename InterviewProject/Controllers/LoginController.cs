using Microsoft.AspNetCore.Mvc;

namespace InterviewProject.Controllers
{
    public class LoginController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}