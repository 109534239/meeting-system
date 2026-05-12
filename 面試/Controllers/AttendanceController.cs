using Microsoft.AspNetCore.Mvc;
using 面試.Data;

namespace 面試.Controllers
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