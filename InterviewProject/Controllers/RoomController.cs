using Microsoft.AspNetCore.Mvc;
using InterviewProject.Data;
using InterviewProject.Models;

namespace InterviewProject.Controllers
{
    public class RoomController : Controller
    {
        private readonly AppDbContext _context;

        public RoomController(AppDbContext context)
        {
            _context = context;
        }

        public IActionResult Index()
        {
            var rooms = _context.Rooms.ToList();
            return View(rooms);
        }

        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        public IActionResult Create(string roomName)
        {
            try
            {
                var room = new Room
                {
                    RoomName = roomName,
                    CreatedTime = DateTime.Now
                };

                _context.Rooms.Add(room);
                _context.SaveChanges();

                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                return Content(ex.ToString()); // 👈 直接把真正錯誤吐出來
            }
        }

        public IActionResult Join(int id)
        {
            ViewBag.RoomId = id;
            return View();
        }
    }
}