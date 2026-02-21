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
            }
        }

        private async Task ProcessNode(ExecutionQueueItem item, Guid executionId)
        {
            _logger.LogInformation($"Processing Node {item.NodeId} ({item.NodeType})...");
            await _hubContext.Clients.Group(executionId.ToString()).SendAsync("NodeStatusChanged", executionId, item.NodeId, "Running");
            
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
            await _hubContext.Clients.Group(executionId.ToString()).SendAsync("NodeStatusChanged", executionId, item.NodeId, status);
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
