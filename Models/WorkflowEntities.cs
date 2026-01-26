using LiteDB;
using System;
using System.Collections.Generic;

namespace DrawflowPlayground.Models
{
    public class WorkflowDefinition
    {
        [BsonId]
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string JsonData { get; set; } // Raw Drawflow JSON
    }

    public class WorkflowExecution
    {
        [BsonId]
        public Guid Id { get; set; }
        public Guid WorkflowId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public ExecutionStatus Status { get; set; }
    }

    public enum ExecutionStatus
    {
        Running,
        Completed,
        Failed
    }

    public class ExecutionQueueItem
    {
        [BsonId]
        public Guid Id { get; set; }
        public Guid ExecutionId { get; set; }
        public string NodeId { get; set; } // Drawflow uses string IDs usually
        public DateTime QueuedAt { get; set; }
        public bool Processed { get; set; }
    }

    public class ExecutionResult
    {
        [BsonId]
        public Guid Id { get; set; }
        public Guid ExecutionId { get; set; }
        public string NodeId { get; set; }
        public string Output { get; set; }
        public DateTime CompletedAt { get; set; }
    }
}
