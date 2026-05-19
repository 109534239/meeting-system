using Microsoft.AspNetCore.Mvc;

public class MemberController : Controller
{
    public IActionResult Profile()
    {
        return View();
    }

    public IActionResult Resume()
    {
        return View();
    }

    public IActionResult Application()
    {
        return View();
    }

    public IActionResult Favorites()
    {
        return View();
    }
}