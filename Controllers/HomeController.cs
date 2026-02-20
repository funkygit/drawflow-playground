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

    public IActionResult List()
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

    [HttpGet]
    public IActionResult GetNodeMeta()
    {
        // For demonstration, reading from the auto-generated config file
        var path = Path.Combine(Directory.GetCurrentDirectory(), "ContextHelpers", "auto-generated.json");
        if (!System.IO.File.Exists(path)) return NotFound("Config file not found.");

        var json = System.IO.File.ReadAllText(path);
        var configs = System.Text.Json.JsonSerializer.Deserialize<List<DrawflowPlayground.Models.NodeConfiguration>>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var metaList = MapNodesToMeta(configs);

        return Ok(metaList);
    }

    private List<DrawflowPlayground.Models.NodeMeta> MapNodesToMeta(List<DrawflowPlayground.Models.NodeConfiguration> configs)
    {
        return configs?.Select(config => new DrawflowPlayground.Models.NodeMeta
        {
            NodeKey = config.NodeKey,
            DisplayName = config.DisplayName,
            NodeType = config.NodeType,
            NodeTypeKey = config.NodeTypeKey,
            NodeTypeOrder = config.NodeTypeOrder,
            VariantSource = config.VariantSource,
            Variants = config.Variants?.Select(v => new DrawflowPlayground.Models.VariantMeta
            {
                Value = v.Value,
                Label = v.Label,
                Parameters = FlattenParameters(v),
                Outputs = v.Outputs
            }).ToList()
        }).ToList() ?? new List<DrawflowPlayground.Models.NodeMeta>();
    }

    private List<DrawflowPlayground.Models.NodeParameterMeta> FlattenParameters(DrawflowPlayground.Models.NodeVariant variant)
    {
        var parameters = new List<DrawflowPlayground.Models.NodeParameter>();
        
        // 1. From Constructor
        if (variant.Constructor?.Parameters != null)
            parameters.AddRange(variant.Constructor.Parameters);

        // 2. From ExecutionFlow
        if (variant.ExecutionFlow != null)
        {
            foreach (var step in variant.ExecutionFlow)
            {
                if (step.Parameters != null)
                    parameters.AddRange(step.Parameters);
            }
        }

        // 3. Map to Meta DTO (Deduplicate by Name if necessary, but here we just project)
        return parameters.Select(p => new DrawflowPlayground.Models.NodeParameterMeta
        {
            Name = p.Name,
            DisplayName = p.DisplayName ?? p.Name,
            DataType = p.DataType,
            Source = p.Source,
            Value = p.Value
        }).ToList();
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
