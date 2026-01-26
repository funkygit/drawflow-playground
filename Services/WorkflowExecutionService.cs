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
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DrawflowPlayground.Services
{
    public class WorkflowExecutionService : BackgroundService
    {
        private readonly LiteDbContext _db;
        private readonly IHubContext<WorkflowHub> _hubContext;
        private readonly ILogger<WorkflowExecutionService> _logger;
        private readonly ConcurrentDictionary<Guid, Task> _activeExecutions = new();
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellationTokens = new();

        public WorkflowExecutionService(LiteDbContext db, IHubContext<WorkflowHub> hubContext, ILogger<WorkflowExecutionService> logger)
        {
            _db = db;
            _hubContext = hubContext;
            _logger = logger;
        }

        public Guid StartWorkflow(Guid workflowId)
        {
            var workflow = _db.WorkflowDefinitions.FindById(workflowId);
            if (workflow == null) throw new Exception("Workflow not found");

            var executionId = Guid.NewGuid();
            var execution = new WorkflowExecution
            {
                Id = executionId,
                WorkflowId = workflowId,
                StartTime = DateTime.UtcNow,
                Status = ExecutionStatus.Running
            };
            _db.WorkflowExecutions.Insert(execution);

            // Parse Drawflow JSON to find Start Node(s)
            // Assuming "Start" node type or nodes with 0 inputs
             var root = JsonNode.Parse(workflow.JsonData);
             // Basic Drawflow structure assumption: drawflow.Home.data.{id}
             var nodes = root["drawflow"]?["Home"]?["data"]?.AsObject();
             
             if (nodes != null)
             {
                 foreach (var kvp in nodes)
                 {
                     var node = kvp.Value;
                     var inputs = node["inputs"]?.AsObject();
                     // Simple logic: If no inputs or type == "start", queue it.
                     if (inputs == null || inputs.Count == 0 || node["name"]?.ToString() == "start")
                     {
                         QueueNode(executionId, node["id"].ToString());
                     }
                 }
             }

            // Start Execution Loop
            var cts = new CancellationTokenSource();
            _cancellationTokens[executionId] = cts;
            var task = Task.Run(() => ExecutionLoop(executionId, workflow.JsonData, cts.Token));
            _activeExecutions[executionId] = task;

            return executionId;
        }

        private void QueueNode(Guid executionId, string nodeId)
        {
            var queueItem = new ExecutionQueueItem
            {
                ExecutionId = executionId,
                NodeId = nodeId,
                QueuedAt = DateTime.UtcNow,
                Processed = false
            };
            _db.ExecutionQueue.Insert(queueItem);
             _logger.LogInformation($"Queued Node {nodeId} for Execution {executionId}");
        }

        private async Task ExecutionLoop(Guid executionId, string workflowJson, CancellationToken token)
        {
             _logger.LogInformation($"Starting Execution Loop for {executionId}");
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 1. Check Queue
                    var queueItem = _db.ExecutionQueue.FindOne(x => x.ExecutionId == executionId && !x.Processed);
                    if (queueItem == null)
                    {
                        // Check if execution is complete
                        // If no processing items and no pending queue items, are we done?
                        // Simple check: If all reachable nodes are visited? 
                        // For now, just wait.
                        await Task.Delay(500, token);
                        continue;
                    }

                    // 2. Process Node
                    await ProcessNode(queueItem, workflowJson, executionId);

                    // 3. Mark Processed
                    queueItem.Processed = true;
                    _db.ExecutionQueue.Update(queueItem);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in Execution {executionId}");
                var exec = _db.WorkflowExecutions.FindById(executionId);
                if (exec != null)
                {
                    exec.Status = ExecutionStatus.Failed;
                    exec.EndTime = DateTime.UtcNow;
                    _db.WorkflowExecutions.Update(exec);
                }
            }
            finally
            {
                 _activeExecutions.TryRemove(executionId, out _);
                 _cancellationTokens.TryRemove(executionId, out _);
            }
        }

        private async Task ProcessNode(ExecutionQueueItem item, string workflowJson, Guid executionId)
        {
            // Simulate work
            _logger.LogInformation($"Processing Node {item.NodeId}...");
            await _hubContext.Clients.Group(executionId.ToString()).SendAsync("NodeStatusChanged", item.NodeId, "Running");
            await Task.Delay(2000); // 2 seconds delay

            // Record Result
            var result = new ExecutionResult
            {
                ExecutionId = executionId,
                NodeId = item.NodeId,
                Output = "Success",
                CompletedAt = DateTime.UtcNow
            };
            _db.ExecutionResults.Insert(result);

            await _hubContext.Clients.Group(executionId.ToString()).SendAsync("NodeStatusChanged", item.NodeId, "Completed");

            // Queue Next Nodes
            var root = JsonNode.Parse(workflowJson);
            var nodeData = root["drawflow"]?["Home"]?["data"]?[item.NodeId];
            
            if (nodeData != null)
            {
                var outputs = nodeData["outputs"]?.AsObject();
                // Output structure: "output_1": { "connections": [ { "node": "2", "output": "input_1" } ] }
                if (outputs != null)
                {
                    var childrenToCheck = new HashSet<string>();
                    foreach (var output in outputs)
                    {
                        var connections = output.Value["connections"]?.AsArray();
                        if (connections != null)
                        {
                            foreach (var conn in connections)
                            {
                                childrenToCheck.Add(conn["node"].ToString());
                            }
                        }
                    }

                    foreach (var childId in childrenToCheck)
                    {
                        if (CanQueue(childId, workflowJson, executionId))
                        {
                            QueueNode(executionId, childId);
                        }
                    }
                }
            }
            
            // Check for End Node (Convention: name="end")
            if (nodeData["name"]?.ToString().ToLower() == "end")
            {
                 var exec = _db.WorkflowExecutions.FindById(executionId);
                 exec.Status = ExecutionStatus.Completed;
                 exec.EndTime = DateTime.UtcNow;
                 _db.WorkflowExecutions.Update(exec);
                 
                 _cancellationTokens[executionId]?.Cancel(); // Stop the loop
            }
        }

        private bool CanQueue(string childId, string workflowJson, Guid executionId)
        {
            // Check if all parents are completed
            var root = JsonNode.Parse(workflowJson);
            var nodes = root["drawflow"]?["Home"]?["data"]?.AsObject();
            var childNode = nodes?[childId];
            if (childNode == null) return false;

            var nodeName = childNode["name"]?.ToString().ToLower();
            bool isLoopOrMerge = nodeName != null && (nodeName.Contains("loop") || nodeName.Contains("merge"));

            var inputs = childNode["inputs"]?.AsObject();
            if (inputs == null) return true;

            bool anyParentReady = false;
            bool allParentsReady = true;
            bool hasInputs = false;

            foreach (var input in inputs)
            {
                var connections = input.Value["connections"]?.AsArray();
                if (connections != null)
                {
                    foreach (var conn in connections)
                    {
                        hasInputs = true;
                        var parentId = conn["node"].ToString();
                        // Check if parent is completed
                        var parentResult = _db.ExecutionResults.FindOne(x => x.ExecutionId == executionId && x.NodeId == parentId);
                        
                        // For loops: We might need to check if it's RECENTLY completed? 
                        // For now, simple existence check. 
                        // Note: execution results accumulate. Logic might need Timestamp check for valid "token".
                        // This is a complex area (Color Petri Nets). 
                        // MVP: Just check existence.
                        
                        if (parentResult == null)
                        {
                            allParentsReady = false;
                        }
                        else
                        {
                            anyParentReady = true;
                        }
                    }
                }
            }
            
            if (!hasInputs) return true;

            if (isLoopOrMerge)
            {
                // If Loop/Merge: Ready if ANY parent is ready.
                if (!anyParentReady) return false;
            }
            else
            {
                // Standard: All parents must be ready.
                if (!allParentsReady) return false;
            }
             
             // Check if already queued/processed to avoid duplicate queuing.
             // For loops, we MUST allow re-queuing.
             // Strategy: Allow queueing if it is NOT currently IN THE QUEUE (Processed=false).
             // If it was processed before, we can re-queue it.
             var existingPending = _db.ExecutionQueue.FindOne(x => x.ExecutionId == executionId && x.NodeId == childId && !x.Processed);
             if (existingPending != null) return false; 
             
            return true;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Main BackgroundService execution.
            // Since we spawn tasks per execution, this might just monitor or do nothing.
            // Or it could resume pending executions from DB on startup.
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
