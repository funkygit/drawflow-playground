using DrawflowPlayground.Models;
using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace DrawflowPlayground.Hubs
{
    public class WorkflowHub : Hub
    {
        private readonly DrawflowPlayground.Services.WorkflowExecutionService _executionService;

        public WorkflowHub(DrawflowPlayground.Services.WorkflowExecutionService executionService)
        {
            _executionService = executionService;
        }

        public async Task JoinWorkflowGroup(string workflowId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, workflowId);
        }

        public async Task LeaveWorkflowGroup(string workflowId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, workflowId);
        }

        public async Task<Guid> StartWorkflow(Guid workflowId, Guid? parentExecutionId = null)
        {
            return _executionService.StartWorkflow(workflowId, parentExecutionId);
        }

        public async Task QueueNode(ExecutionQueueItem item)
        {
            _executionService.QueueNode(item);
        }

        public async Task PauseExecution(Guid executionId)
        {
            _executionService.PauseExecution(executionId);
             await Clients.Group(executionId.ToString()).SendAsync("ExecutionPaused", executionId);
        }

        public async Task ResumeExecution(Guid executionId)
        {
            _executionService.ResumeExecution(executionId);
             await Clients.Group(executionId.ToString()).SendAsync("ExecutionResumed", executionId);
        }

        public async Task AbortExecution(Guid executionId)
        {
            _executionService.AbortExecution(executionId);
             await Clients.Group(executionId.ToString()).SendAsync("ExecutionAborted", executionId);
        }
    }
}
