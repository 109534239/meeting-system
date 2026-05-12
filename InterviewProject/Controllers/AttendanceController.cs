using Microsoft.AspNetCore.Mvc;
using InterviewProject.Data;

namespace InterviewProject.Controllers
{
    public class AttendanceController : Controller
    {
        private readonly AppDbContext _context;

        public AttendanceController(AppDbContext context)
        {
            _context = context;
        }

        public IActionResult Index()
        {
            var data = _context.Attendances
                .OrderByDescending(x => x.JoinTime)
                .ToList();

            return View(data);
        }
    }
}