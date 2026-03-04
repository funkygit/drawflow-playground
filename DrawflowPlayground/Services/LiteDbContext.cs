using LiteDB;
using DrawflowPlayground.Models;
using Microsoft.Extensions.Options;

namespace DrawflowPlayground.Services
{
    public class LiteDbContext
    {
        public LiteDatabase Database { get; }

        public LiteDbContext()
        {
            Database = new LiteDatabase("WorkflowData.db");
        }

        public ILiteCollection<WorkflowDefinition> WorkflowDefinitions => Database.GetCollection<WorkflowDefinition>("workflows");
        public ILiteCollection<WorkflowExecution> WorkflowExecutions => Database.GetCollection<WorkflowExecution>("executions");
        public ILiteCollection<ExecutionQueueItem> ExecutionQueue => Database.GetCollection<ExecutionQueueItem>("queue");
        public ILiteCollection<ExecutionResult> ExecutionResults => Database.GetCollection<ExecutionResult>("results");
    }
}
