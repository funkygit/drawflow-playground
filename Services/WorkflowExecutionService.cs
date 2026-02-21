using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DrawflowPlayground.Hubs;
using DrawflowPlayground.Models;
using DrawflowPlayground.Utilities; // Added
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DrawflowPlayground.Services
{
    public class WorkflowExecutionService : BackgroundService
    {
        private readonly LiteDbContext _db;
        private readonly IHubContext<WorkflowHub> _hubContext;
        private readonly IDynamicExecutor _dynamicExecutor;
        private readonly ILogger<WorkflowExecutionService> _logger;
        private readonly ConcurrentDictionary<Guid, Task> _activeExecutions = new();
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellationTokens = new();
        private readonly ConcurrentDictionary<Guid, ManualResetEventSlim> _pauseEvents = new();
        
        // Track active node instances for LongRunning nodes: ExecutionId -> NodeId -> Instance
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, object>> _activeNodeInstances = new();

        // Track iterator state: "executionId_nodeId" -> IteratorState
        private readonly ConcurrentDictionary<string, IteratorState> _iteratorStates = new();

        public WorkflowExecutionService(LiteDbContext db, IHubContext<WorkflowHub> hubContext, IDynamicExecutor dynamicExecutor, ILogger<WorkflowExecutionService> logger)
        {
            _db = db;
            _hubContext = hubContext;
            _dynamicExecutor = dynamicExecutor;
            _logger = logger;
        }

        public Guid StartWorkflow(Guid workflowId, Guid? parentExecutionId = null)
        {
            var executionId = Guid.NewGuid();
            var execution = new WorkflowExecution
            {
                Id = executionId,
                WorkflowId = workflowId,
                ParentExecutionId = parentExecutionId,
                StartTime = DateTime.UtcNow,
                Status = ExecutionStatus.Running
            };
            _db.WorkflowExecutions.Insert(execution);

            // Initialize control structures
            var cts = new CancellationTokenSource();
            _cancellationTokens[executionId] = cts;
            _pauseEvents[executionId] = new ManualResetEventSlim(true);
            _activeNodeInstances[executionId] = new ConcurrentDictionary<string, object>();

            var task = Task.Run(() => ExecutionLoop(executionId, cts.Token));
            _activeExecutions[executionId] = task;

            return executionId;
        }

        private List<NodeConfiguration> _masterConfigs;

        private void LoadMasterConfigs()
        {
            try
            {
                var path = Path.Combine(Directory.GetCurrentDirectory(), "ContextHelpers", "auto-generated.json");
                if (System.IO.File.Exists(path))
                {
                    var json = System.IO.File.ReadAllText(path);
                    _masterConfigs = JsonSerializer.Deserialize<List<NodeConfiguration>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load master configs.");
            }
        }

        public void QueueNode(ExecutionQueueItem item)
        {
            if (!_activeExecutions.ContainsKey(item.ExecutionId)) return;
            if (_masterConfigs == null) LoadMasterConfigs();

            // Fetch config from master config by NodeKey
            NodeConfiguration config = null;
            var master = _masterConfigs?.FirstOrDefault(m => m.NodeKey == item.NodeKey);
            if (master != null)
            {
                // Clone master config to avoid mutations
                config = JsonSerializer.Deserialize<NodeConfiguration>(JsonSerializer.Serialize(master));

                // Apply user parameters from the queue item
                ApplyUserParameters(config, item.Parameters);
            }

            item.Configuration = config;
            item.QueuedAt = DateTime.UtcNow;
            item.Processed = false;
            _db.ExecutionQueue.Insert(item);
            _logger.LogInformation($"Queued Node {item.NodeId} ({item.NodeType}) for Execution {item.ExecutionId}");
        }

        private void ApplyUserParameters(NodeConfiguration config, List<NodeParameterMeta> parameters)
        {
            // Resolve the target variant
            NodeVariant variant = null;
            if (!string.IsNullOrEmpty(config.VariantSource))
            {
                // Find the variant value from the parameters list (variant source is passed as a parameter)
                var variantParam = parameters?.FirstOrDefault(p => p.Name == config.VariantSource);
                var selectedVariant = variantParam?.Value?.ToString();

                if (!string.IsNullOrEmpty(selectedVariant))
                {
                    variant = config.Variants?.FirstOrDefault(v => v.Value == selectedVariant);
                }
            }
            else
            {
                // Non-variant node: use the first (and only) variant
                variant = config.Variants?.FirstOrDefault();
            }

            if (variant == null) return;

            // Inject parameters from the queue item into the variant's Constructor and ExecutionFlow
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    UpdateParameterValue(variant.Constructor?.Parameters, param.Name, param.Value?.ToString());
                    if (variant.ExecutionFlow != null)
                    {
                        foreach (var m in variant.ExecutionFlow)
                            UpdateParameterValue(m.Parameters, param.Name, param.Value?.ToString());
                    }
                }
            }
        }

        private void UpdateParameterValue(List<NodeParameter> parameters, string name, string value)
        {
            if (parameters == null) return;
            var p = parameters.FirstOrDefault(x => x.Name == name);
            if (p != null)
            {
                p.Value = value;
                p.Source = "Constant"; // Treat as constant once resolved from UI
            }
        }

        public void PauseExecution(Guid executionId)
        {
            if (_pauseEvents.TryGetValue(executionId, out var pauseEvent))
            {
                pauseEvent.Reset(); 
                UpdateStatus(executionId, ExecutionStatus.Running); 
            }
        }

        public void ResumeExecution(Guid executionId)
        {
            if (_pauseEvents.TryGetValue(executionId, out var pauseEvent))
            {
                pauseEvent.Set();
            }
        }

        public void AbortExecution(Guid executionId)
        {
            if (_cancellationTokens.TryGetValue(executionId, out var cts))
            {
                cts.Cancel();
                UpdateStatus(executionId, ExecutionStatus.Failed);
                
                // Stop all active nodes
                if (_activeNodeInstances.TryGetValue(executionId, out var nodes))
                {
                    foreach (var nodeId in nodes.Keys)
                    {
                        StopNode(executionId, nodeId).Wait();
                    }
                }
            }
        }

        public async Task StopNode(Guid executionId, string nodeId)
        {
            // Find config to get OnStop method
            // In a real app we might cache config with instance, but here we fetch or need it passed.
            // For now, let's assume we can get it or we just look into instance type if we rely on convention?
            // Actually, we need the config to know the "OnStop" method name.
            // We should probably store the Config with the instance.
            
            if (_activeNodeInstances.TryGetValue(executionId, out var nodes) && nodes.TryRemove(nodeId, out var instance))
            {
                 // We need to retrieve the config again or store it.
                 // Fetching from queue/history is hard.
                 // OPTIMIZATION: Store "NodeContext" { Instance, Configuration } instead of just Instance.
                 // For this step, I will assume we can't easily get the specific config unless we persisted it.
                 // I'll skip the "Dynamic OnStop" for this exact moment and just Dispose if IDisposable?
                 // OR, re-fetch via Workflow Definition (expensive but correct).
                 
                 var execution = _db.WorkflowExecutions.FindById(executionId);
                 var workflow = _db.WorkflowDefinitions.FindById(execution.WorkflowId);
                 // JSON parse to find node... brittle.
                 
                 // Fallback: If instance is IDisposable, dispose it.
                 if (instance is IDisposable disposable)
                 {
                     disposable.Dispose();
                 }
            }
        }

        private void UpdateStatus(Guid executionId, ExecutionStatus status)
        {
             var exec = _db.WorkflowExecutions.FindById(executionId);
             if (exec != null)
             {
                 exec.Status = status;
                 if (status == ExecutionStatus.Completed || status == ExecutionStatus.Failed)
                 {
                     exec.EndTime = DateTime.UtcNow;
                 }
                 _db.WorkflowExecutions.Update(exec);
             }
        }

        private async Task ExecutionLoop(Guid executionId, CancellationToken token)
        {
             _logger.LogInformation($"Starting Execution Loop for {executionId}");
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_pauseEvents.TryGetValue(executionId, out var pauseEvent))
                    {
                        pauseEvent.Wait(token);
                    }

                    var queueItem = _db.ExecutionQueue.FindOne(x => x.ExecutionId == executionId && !x.Processed);
                    if (queueItem == null)
                    {
                        await Task.Delay(500, token);
                        continue;
                    }

                    await ProcessNode(queueItem, executionId);

                    queueItem.Processed = true;
                    _db.ExecutionQueue.Update(queueItem);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"Execution {executionId} Aborted");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in Execution {executionId}");
                UpdateStatus(executionId, ExecutionStatus.Failed);
            }
            finally
            {
                 _activeExecutions.TryRemove(executionId, out _);
                 _cancellationTokens.TryRemove(executionId, out _);
                 _pauseEvents.TryRemove(executionId, out _);
                 _activeNodeInstances.TryRemove(executionId, out _);
                 // Clean up iterator states for this execution
                 foreach (var key in _iteratorStates.Keys.Where(k => k.StartsWith(executionId.ToString())))
                     _iteratorStates.TryRemove(key, out _);
            }
        }

        private async Task ProcessNode(ExecutionQueueItem item, Guid executionId)
        {
            _logger.LogInformation($"Processing Node {item.NodeId} ({item.NodeType})...");
            await _hubContext.Clients.Group(executionId.ToString()).SendAsync("NodeStatusChanged", executionId, item.NodeId, "Running", (string)null);

            // BuiltIn nodes are handled inline — no DLL loading
            if (item.Configuration?.ExecutionMode == "BuiltIn")
            {
                await ProcessBuiltInNode(item, executionId);
                return;
            }
            
            string output = "Success";
            bool isLongRunning = false;

            try
            {
                if (item.Configuration != null)
                {
                    var config = item.Configuration;
                    var inputs = new Dictionary<string, object>(); 

                    // 0. Resolve Variant first (needed for all subsequent steps)
                    NodeVariant resolvedVariant = null;
                    if (string.IsNullOrEmpty(config.VariantSource))
                    {
                        // Non-variant node: use the first (default) variant
                        resolvedVariant = config.Variants?.FirstOrDefault();
                    }
                    // else: multi-variant — resolved in step 2 after inputs are populated
                    if (config.ExecutionFlow != null)
                    {
                        foreach (var m in config.ExecutionFlow)
                        {
                            if (m.Parameters != null)
                            {
                                foreach (var p in m.Parameters.Where(p => p.Source == "Constant"))
                                    inputs[p.Name] = p.Value;
                            }
                        }
                    }
                    
                    // 1. Resolve Input from QueueItem
                    if (item.Input != null)
                    {
                        var nodeInput = item.Input;
                        _logger.LogInformation($"Resolving Input for Node {item.NodeId} from Source {nodeInput.SourceNodeId}");

                        // Fetch Source Result
                        var sourceResult = _db.ExecutionResults.FindOne(r => r.ExecutionId == executionId && r.NodeId == nodeInput.SourceNodeId);
                         
                        if (sourceResult != null)
                        {
                             // Parse Output (Expecting JSON object string)
                             try 
                             {
                                 var outputs = JsonNode.Parse(sourceResult.Output);
                                 var specificOutput = outputs?[nodeInput.SourceOutputName];
                                 
                                 if (specificOutput != null)
                                 {
                                     object resolvedValue = specificOutput.ToString();
                                      if (specificOutput.GetValueKind() == JsonValueKind.String) resolvedValue = specificOutput.GetValue<string>();
                                      else if (specificOutput.GetValueKind() == JsonValueKind.Number) resolvedValue = specificOutput.GetValue<int>();
                                      else if (specificOutput.GetValueKind() == JsonValueKind.True || specificOutput.GetValueKind() == JsonValueKind.False) resolvedValue = specificOutput.GetValue<bool>();
                                      
                                     // Map to parameters expecting NodeOutput
                                     if (config.Lifecycle?.OnStart?.Parameters != null)
                                     {
                                         foreach (var p in config.Lifecycle.OnStart.Parameters.Where(p => p.Source == "NodeOutput"))
                                         {
                                             inputs[p.Name] = resolvedValue;
                                         }
                                     }
                                     if (config.ExecutionFlow != null)
                                     {
                                         foreach (var m in config.ExecutionFlow)
                                         {
                                             if (m.Parameters != null)
                                             {
                                                 foreach (var p in m.Parameters.Where(p => p.Source == "NodeOutput"))
                                                 {
                                                     inputs[p.Name] = resolvedValue;
                                                 }
                                             }
                                         }
                                     }
                                 }
                                 else
                                 {
                                     _logger.LogWarning($"Output '{nodeInput.SourceOutputName}' not found in result of node {nodeInput.SourceNodeId}");
                                 }
                             }
                             catch (Exception ex)
                             {
                                 _logger.LogError(ex, "Failed to parse source result output.");
                             }
                        }
                        else
                        {
                              _logger.LogWarning($"Result not found for source node {nodeInput.SourceNodeId}");
                        }
                    }

                    // 2. Resolve Variant (Multi-variant nodes)
                    if (!string.IsNullOrEmpty(config.VariantSource) && config.Variants != null)
                    {
                        if (inputs.TryGetValue(config.VariantSource, out var variantValue))
                        {
                            resolvedVariant = config.Variants.FirstOrDefault(v => v.Value == variantValue?.ToString());
                            if (resolvedVariant != null)
                            {
                                _logger.LogInformation($"Applying Variant: {resolvedVariant.Label} ({resolvedVariant.Value})");
                            }
                            else
                            {
                                _logger.LogWarning($"Variant not found for value '{variantValue}' (Source: {config.VariantSource})");
                            }
                        }
                    }

                    // Extract resolved properties from variant
                    var resolvedTypeName = resolvedVariant?.TypeName;
                    var resolvedConstructor = resolvedVariant?.Constructor;
                    var resolvedExecutionFlow = resolvedVariant?.ExecutionFlow ?? config.ExecutionFlow;
                    var resolvedOutputs = resolvedVariant?.Outputs ?? config.Outputs;

                    // Initialize Inputs from Resolved Constants
                    if (resolvedConstructor?.Parameters != null)
                    {
                        foreach (var p in resolvedConstructor.Parameters.Where(p => p.Source == "Constant"))
                            inputs[p.Name] = p.Value;
                    }
                    if (resolvedExecutionFlow != null)
                    {
                        foreach (var m in resolvedExecutionFlow)
                        {
                            if (m.Parameters != null)
                            {
                                foreach (var p in m.Parameters.Where(p => p.Source == "Constant"))
                                    inputs[p.Name] = p.Value;
                            }
                        }
                    }

                    if (config.ExecutionMode == "LongRunning" && config.Lifecycle != null)
                    {
                        isLongRunning = true;
                        _logger.LogInformation($"Starting LongRunning Node {item.NodeId}");
                        
                        // 1. Create Instance
                        var instance = _dynamicExecutor.CreateInstance(config.DllPath, resolvedTypeName, resolvedConstructor, inputs);
                        
                        // 2. Execute OnStart
                        if (config.Lifecycle.OnStart != null)
                        {
                            await _dynamicExecutor.ExecuteMethodAsync(instance, config.Lifecycle.OnStart, inputs);
                        }
                        
                        // 3. Store Instance
                        if (_activeNodeInstances.TryGetValue(executionId, out var nodes))
                        {
                            nodes[item.NodeId] = instance; // Store for OnStop/OnEvent
                            // TODO: Store Config here too for OnStop
                        }
                        
                        output = "Started (Long Running)";
                    }
                    else if (resolvedExecutionFlow != null)
                    {
                        // Transient
                        _logger.LogInformation($"Executing Transient Node {item.NodeId}");
                        var instance = _dynamicExecutor.CreateInstance(config.DllPath, resolvedTypeName, resolvedConstructor, inputs);
                        
                        object lastResult = null;
                        foreach (var method in resolvedExecutionFlow.OrderBy(m => m.Sequence))
                        {
                            lastResult = await _dynamicExecutor.ExecuteMethodAsync(instance, method, inputs);
                        }
                        output = lastResult?.ToString() ?? "Completed";
                        
                        if (instance is IDisposable d) d.Dispose();
                    }
                }
                else
                {
                    // Legacy Fallback
                     await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error executing node {item.NodeId}");
                output = $"Error: {ex.Message}";
            }

            // Record Result
            var result = new ExecutionResult
            {
                ExecutionId = executionId,
                NodeId = item.NodeId,
                Output = output,
                CompletedAt = DateTime.UtcNow
            };
            _db.ExecutionResults.Insert(result);

            // Only mark "Completed" visually if it's NOT LongRunning, OR if we want to show it's "Ready". 
            // Usually LongRunning might show "Active".
            var status = isLongRunning ? "Active" : "Completed";
            await _hubContext.Clients.Group(executionId.ToString()).SendAsync("NodeStatusChanged", executionId, item.NodeId, status, (string)null);
        }

        // ========== Built-In Node Handlers ==========

        private async Task ProcessBuiltInNode(ExecutionQueueItem item, Guid executionId)
        {
            string output = "Success";
            string triggeredOutput = null; // null = queue all children, specific = route to one output

            try
            {
                switch (item.NodeKey)
                {
                    case "end":
                        output = HandleEndNode(executionId);
                        break;
                    case "loop":
                        output = "PassThrough";
                        break;
                    case "conditional":
                        (output, triggeredOutput) = HandleConditionalNode(item, executionId);
                        break;
                    case "iterator":
                        (output, triggeredOutput) = HandleIteratorNode(item, executionId);
                        break;
                    default:
                        _logger.LogWarning($"Unknown BuiltIn node key: {item.NodeKey}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error executing BuiltIn node {item.NodeId} ({item.NodeKey})");
                output = $"Error: {ex.Message}";
            }

            // Record result
            _db.ExecutionResults.Insert(new ExecutionResult
            {
                ExecutionId = executionId,
                NodeId = item.NodeId,
                Output = output,
                CompletedAt = DateTime.UtcNow
            });

            await _hubContext.Clients.Group(executionId.ToString())
                .SendAsync("NodeStatusChanged", executionId, item.NodeId, "Completed", triggeredOutput);
        }

        private string HandleEndNode(Guid executionId)
        {
            _logger.LogInformation($"End Node reached for Execution {executionId}. Marking completed.");
            UpdateStatus(executionId, ExecutionStatus.Completed);

            // Clean up iterator states for this execution
            foreach (var key in _iteratorStates.Keys.Where(k => k.StartsWith(executionId.ToString())))
                _iteratorStates.TryRemove(key, out _);

            return "Workflow Completed";
        }

        private (string output, string triggeredOutput) HandleConditionalNode(ExecutionQueueItem item, Guid executionId)
        {
            // Resolve LHS from a preceding node's output
            var lhsValue = ResolveParameterFromResult(item, executionId, "lhsSource");
            var op = item.Parameters?.FirstOrDefault(p => p.Name == "operator")?.Value?.ToString();
            var rhs = item.Parameters?.FirstOrDefault(p => p.Name == "rhsValue")?.Value?.ToString();

            _logger.LogInformation($"Conditional: LHS='{lhsValue}' {op} RHS='{rhs}'");
            bool result = EvaluateCondition(lhsValue, op, rhs);

            var branch = result ? "output_1" : "output_2";
            _logger.LogInformation($"Conditional result: {result} → routing to {branch}");
            return ($"Condition: {result}", branch);
        }

        private (string output, string triggeredOutput) HandleIteratorNode(ExecutionQueueItem item, Guid executionId)
        {
            var stateKey = $"{executionId}_{item.NodeId}";

            if (!_iteratorStates.TryGetValue(stateKey, out var state))
            {
                // First invocation — initialize state
                var mode = item.Parameters?.FirstOrDefault(p => p.Name == "iterationMode")?.Value?.ToString() ?? "Count";
                state = new IteratorState { Mode = mode, CurrentIndex = 0 };

                if (mode == "Collection")
                {
                    var collectionJson = ResolveParameterFromResult(item, executionId, "sourceCollection");
                    try
                    {
                        state.Items = JsonSerializer.Deserialize<List<string>>(collectionJson ?? "[]");
                    }
                    catch
                    {
                        // If it's not a JSON array, wrap single value
                        state.Items = new List<string> { collectionJson ?? "" };
                    }
                    _logger.LogInformation($"Iterator initialized (Collection mode): {state.Items.Count} items");
                }
                else
                {
                    int.TryParse(item.Parameters?.FirstOrDefault(p => p.Name == "count")?.Value?.ToString(), out var count);
                    state.TotalCount = count;
                    _logger.LogInformation($"Iterator initialized (Count mode): {count} iterations");
                }

                _iteratorStates[stateKey] = state;
            }

            if (!state.IsComplete)
            {
                var currentItem = state.Mode == "Collection"
                    ? state.Items[state.CurrentIndex]
                    : state.CurrentIndex.ToString();
                state.CurrentIndex++;

                _logger.LogInformation($"Iterator [{state.CurrentIndex}/{(state.Mode == "Collection" ? state.Items.Count : state.TotalCount)}]: {currentItem}");
                // Store current item as JSON output for downstream nodes
                return ($"{{\"CurrentItem\":\"{currentItem}\",\"Index\":{state.CurrentIndex - 1}}}", "output_1");
            }
            else
            {
                _logger.LogInformation($"Iterator complete for {item.NodeId}. Routing to exit.");
                _iteratorStates.TryRemove(stateKey, out _);
                return ("Iteration Complete", "output_2");
            }
        }

        private bool EvaluateCondition(string lhs, string op, string rhs)
        {
            if (lhs == null || op == null) return false;

            // Try numeric comparison first
            if (double.TryParse(lhs, out var lhsNum) && double.TryParse(rhs, out var rhsNum))
            {
                return op switch
                {
                    "Equals" => lhsNum == rhsNum,
                    "NotEquals" => lhsNum != rhsNum,
                    "GreaterThan" => lhsNum > rhsNum,
                    "LessThan" => lhsNum < rhsNum,
                    _ => false
                };
            }

            // String comparison fallback
            var comparison = string.Compare(lhs, rhs, StringComparison.OrdinalIgnoreCase);
            return op switch
            {
                "Equals" => comparison == 0,
                "NotEquals" => comparison != 0,
                "GreaterThan" => comparison > 0,
                "LessThan" => comparison < 0,
                _ => false
            };
        }

        /// <summary>
        /// Resolves a NODE_OUTPUT parameter value from a preceding node's stored result.
        /// The parameter value format is "nodeId.outputName" (set by the NODE_OUTPUT select in the UI).
        /// </summary>
        private string ResolveParameterFromResult(ExecutionQueueItem item, Guid executionId, string paramName)
        {
            var param = item.Parameters?.FirstOrDefault(p => p.Name == paramName);
            if (param == null || string.IsNullOrEmpty(param.Value?.ToString())) return null;

            // Value format: "nodeId.outputName"
            var raw = param.Value.ToString();
            var dotIndex = raw.IndexOf('.');
            if (dotIndex < 0) return null;

            var sourceNodeId = raw.Substring(0, dotIndex);
            var outputName = raw.Substring(dotIndex + 1);

            var sourceResult = _db.ExecutionResults.FindOne(r => r.ExecutionId == executionId && r.NodeId == sourceNodeId);
            if (sourceResult == null)
            {
                _logger.LogWarning($"Result not found for source node {sourceNodeId}");
                return null;
            }

            try
            {
                var outputs = JsonNode.Parse(sourceResult.Output);
                return outputs?[outputName]?.ToString();
            }
            catch
            {
                // If output is not JSON, return raw
                return sourceResult.Output;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
