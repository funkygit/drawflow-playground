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
        private readonly ConcurrentDictionary<Guid, ManualResetEventSlim> _pauseEvents = new();

        public WorkflowExecutionService(LiteDbContext db, IHubContext<WorkflowHub> hubContext, ILogger<WorkflowExecutionService> logger)
        {
            _db = db;
            _hubContext = hubContext;
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
            _pauseEvents[executionId] = new ManualResetEventSlim(true); // Initially set (not paused)

            var task = Task.Run(() => ExecutionLoop(executionId, cts.Token));
            _activeExecutions[executionId] = task;

            return executionId;
        }

        public void QueueNode(Guid executionId, string nodeId, string nodeType = "action")
        {
            // Only allow queuing if execution is active
            if (!_activeExecutions.ContainsKey(executionId)) return;

            var queueItem = new ExecutionQueueItem
            {
                ExecutionId = executionId,
                NodeId = nodeId,
                NodeType = nodeType,
                QueuedAt = DateTime.UtcNow,
                Processed = false
            };
            _db.ExecutionQueue.Insert(queueItem);
             _logger.LogInformation($"Queued Node {nodeId} ({nodeType}) for Execution {executionId}");
        }

        public void PauseExecution(Guid executionId)
        {
            if (_pauseEvents.TryGetValue(executionId, out var pauseEvent))
            {
                pauseEvent.Reset(); // Blocks threads waiting on this
                UpdateStatus(executionId, ExecutionStatus.Running); // Maybe a paused status? keeping simple.
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
                    // Check Pause logic
                    if (_pauseEvents.TryGetValue(executionId, out var pauseEvent))
                    {
                        pauseEvent.Wait(token); // Block if paused
                    }

                    // 1. Check Queue
                    var queueItem = _db.ExecutionQueue.FindOne(x => x.ExecutionId == executionId && !x.Processed);
                    if (queueItem == null)
                    {
                        await Task.Delay(500, token);
                        continue;
                    }

                    // 2. Process Node
                    await ProcessNode(queueItem, executionId);

                    // 3. Mark Processed
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
            }
        }

        private async Task ProcessNode(ExecutionQueueItem item, Guid executionId)
        {
            _logger.LogInformation($"Processing Node {item.NodeId} ({item.NodeType})...");
            await _hubContext.Clients.Group(executionId.ToString()).SendAsync("NodeStatusChanged", executionId, item.NodeId, "Running");
            
            if (item.NodeType?.ToLower() == "delay")
            {
                 await Task.Delay(2000); 
            }
            else
            {
                 await Task.Delay(500); // Standard speed
            }

            // Record Result
            var result = new ExecutionResult
            {
                ExecutionId = executionId,
                NodeId = item.NodeId,
                Output = "Success",
                CompletedAt = DateTime.UtcNow
            };
            _db.ExecutionResults.Insert(result);

            await _hubContext.Clients.Group(executionId.ToString()).SendAsync("NodeStatusChanged", executionId, item.NodeId, "Completed");

            // NOTE: No longer queueing next nodes here. Client will listen to "Completed" and queue next.
            // Check for End Node logic if we want Server to know when to "officially" stop?
            // Or Client can send "Stop" command?
            // For now, let's keep the thread running until Client sends Abort or we timeout (not implemented).
            // Or if we encounter a special "End" signal from client?
            // Let's assume user manually aborts or we add a "CompleteExecution" hub method later. 
            // Actually, if it's the "End" node, the Client should call "CompleteWorkflow" or similar.
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
