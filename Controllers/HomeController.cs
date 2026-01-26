using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using drawflow_playground.Models;

namespace drawflow_playground.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly DrawflowPlayground.Services.LiteDbContext _db;

    public HomeController(ILogger<HomeController> logger, DrawflowPlayground.Services.LiteDbContext db)
    {
        _logger = logger;
        _db = db;
    }

    public IActionResult Index()
    {
        return View();
    }

    [HttpPost]
    public IActionResult SaveWorkflow([FromBody] DrawflowPlayground.Models.WorkflowDefinition workflow)
    {
        if (workflow.Id == Guid.Empty) workflow.Id = Guid.NewGuid();
        _db.WorkflowDefinitions.Upsert(workflow);
        return Ok(new { id = workflow.Id });
    }
    
    [HttpGet]
    public IActionResult GetWorkflows()
    {
         return Ok(_db.WorkflowDefinitions.FindAll());
    }

    [HttpGet]
    public IActionResult GetWorkflow(Guid id)
    {
         return Ok(_db.WorkflowDefinitions.FindById(id));
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
