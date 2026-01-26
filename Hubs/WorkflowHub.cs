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

        public async Task<Guid> StartWorkflow(Guid workflowId)
        {
            // In a real app, you might want to return the ExecutionId immediately or handle errors
            return _executionService.StartWorkflow(workflowId);
        }
    }
}
