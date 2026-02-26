using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using drawflow_playground.Models;

namespace drawflow_playground.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly DrawflowPlayground.Services.LiteDbContext _db;
    private readonly DrawflowPlayground.Utilities.IDynamicExecutor _dynamicExecutor;

    public HomeController(ILogger<HomeController> logger, DrawflowPlayground.Services.LiteDbContext db, DrawflowPlayground.Utilities.IDynamicExecutor dynamicExecutor)
    {
        _logger = logger;
        _db = db;
        _dynamicExecutor = dynamicExecutor;
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

    [HttpPost]
    public async Task<IActionResult> TestNode([FromBody] DrawflowPlayground.Models.TestNodeRequest request)
    {
        try
        {
            // Load master config
            var path = Path.Combine(Directory.GetCurrentDirectory(), "ContextHelpers", "auto-generated.json");
            if (!System.IO.File.Exists(path)) return BadRequest(new { success = false, error = "Config file not found." });

            var json = System.IO.File.ReadAllText(path);
            var configs = System.Text.Json.JsonSerializer.Deserialize<List<DrawflowPlayground.Models.NodeConfiguration>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var config = configs?.FirstOrDefault(c => c.NodeKey == request.NodeKey);
            if (config == null) return BadRequest(new { success = false, error = $"Node '{request.NodeKey}' not found in config." });

            if (config.ExecutionMode == "BuiltIn")
                return BadRequest(new { success = false, error = "BuiltIn nodes cannot be tested individually." });

            // Resolve variant
            DrawflowPlayground.Models.NodeVariant variant = null;
            if (!string.IsNullOrEmpty(request.VariantValue) && config.Variants != null)
                variant = config.Variants.FirstOrDefault(v => v.Value == request.VariantValue);
            variant ??= config.Variants?.FirstOrDefault();

            if (variant == null)
                return BadRequest(new { success = false, error = "No variant found for this node." });

            var typeName = variant.TypeName;
            var constructorDef = variant.Constructor;
            var executionFlow = variant.ExecutionFlow ?? config.ExecutionFlow;

            if (string.IsNullOrEmpty(typeName))
                return BadRequest(new { success = false, error = "No TypeName in variant. Cannot execute." });

            // Build inputs dictionary from request params
            var inputs = new Dictionary<string, object>();
            if (request.Parameters != null)
            {
                foreach (var kvp in request.Parameters)
                    inputs[kvp.Key] = kvp.Value;
            }

            // Also inject constant values from config that aren't overridden
            if (constructorDef?.Parameters != null)
            {
                foreach (var p in constructorDef.Parameters.Where(p => p.Source == "Constant" && !inputs.ContainsKey(p.Name)))
                    inputs[p.Name] = p.Value;
            }
            if (executionFlow != null)
            {
                foreach (var m in executionFlow)
                {
                    if (m.Parameters != null)
                    {
                        foreach (var p in m.Parameters.Where(p => p.Source == "Constant" && !inputs.ContainsKey(p.Name)))
                            inputs[p.Name] = p.Value;
                    }
                }
            }

            // Execute via DynamicExecutor
            var instance = _dynamicExecutor.CreateInstance(config.DllPath, typeName, constructorDef, inputs);

            object lastResult = null;
            var methodResults = new List<object>();

            if (executionFlow != null && executionFlow.Count > 0)
            {
                foreach (var method in executionFlow.OrderBy(m => m.Sequence))
                {
                    lastResult = await _dynamicExecutor.ExecuteMethodAsync(instance, method, inputs);
                    methodResults.Add(new { method = method.MethodName, result = lastResult });
                }
            }

            if (instance is IDisposable disposable) disposable.Dispose();

            return Ok(new { success = true, output = lastResult?.ToString(), steps = methodResults });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TestNode failed for {NodeKey}", request.NodeKey);
            return Ok(new { success = false, error = ex.Message, innerError = ex.InnerException?.Message });
        }
    }

    [HttpGet]
    public IActionResult GetNodeMeta()
    {
        // For demonstration, reading from the auto-generated config file
        var path = Path.Combine(Directory.GetCurrentDirectory(), "ContextHelpers", "auto-generated.json");
        if (!System.IO.File.Exists(path)) return NotFound("Config file not found.");

        var json = System.IO.File.ReadAllText(path);
        var configs = System.Text.Json.JsonSerializer.Deserialize<List<DrawflowPlayground.Models.NodeConfiguration>>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (configs == null) return BadRequest("Failed to deserialize node configurations.");

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
            }).ToList() ?? []
        }).ToList() ?? [];
    }

    private static List<DrawflowPlayground.Models.NodeParameterMeta> FlattenParameters(DrawflowPlayground.Models.NodeVariant variant)
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
        Value = p.Value,
        AllowedValues = p.AllowedValues,
        VisibleWhen = p.VisibleWhen
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
