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
        public Guid? ParentExecutionId { get; set; }
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
        public string NodeType { get; set; }
        public NodeConfiguration Configuration { get; set; }
        public NodeInput Input { get; set; }
        public DateTime QueuedAt { get; set; }
        public bool Processed { get; set; }
    }

    public class NodeConfiguration
    {
        public string NodeKey { get; set; }
        public string DisplayName { get; set; }
        public string NodeType { get; set; } // "Input", "Processing", "Output"
        public string NodeTypeKey { get; set; } // "input", "processing", "output"
        public int NodeTypeOrder { get; set; } // 1, 2, 3...
        public string ExecutionMode { get; set; } // "LongRunning" or "Transient"
        public string DllPath { get; set; }
        public string TypeName { get; set; }
        
        public MethodDefinition Constructor { get; set; }

        // For Variant-Based Mapping
        public string VariantSource { get; set; }
        public List<NodeVariant> Variants { get; set; }

        // For LongRunning
        public NodeLifecycle Lifecycle { get; set; }
        
        // For Transient
        public List<MethodDefinition> ExecutionFlow { get; set; }
        
        public List<NodeOutput> Outputs { get; set; }
    }

    public class NodeVariant
    {
        public string Value { get; set; }
        public string Label { get; set; }
        public string TypeName { get; set; }
        public MethodDefinition Constructor { get; set; }
        public List<MethodDefinition> ExecutionFlow { get; set; }
        public List<NodeOutput> Outputs { get; set; }
    }

    public class NodeLifecycle
    {
        public MethodDefinition OnStart { get; set; }
        public MethodDefinition OnEvent { get; set; }
        public MethodDefinition OnStop { get; set; }
    }

    public class MethodDefinition
    {
        public int Sequence { get; set; }
        public string MethodName { get; set; }
        public List<NodeParameter> Parameters { get; set; }
        public List<string> Emits { get; set; }
    }

    public class NodeParameter
    {
        public string Name { get; set; }
        public string DisplayName { get; set; } // For UI Labels
        public int Order { get; set; }
        public string DataType { get; set; }
        public List<string> AllowedValues { get; set; }
        public object IsRequired { get; set; } // specific parsing logic needed
        public string Source { get; set; }
        public object Value { get; set; } // Constant value from config
        public object DefaultValue { get; set; } 
        public bool HasDefaultValue => DefaultValue != null;
        public Type ParameterType => Type.GetType(DataType) ?? typeof(object);
    }
    
    public class ParameterRequirement
    {
        public string Type { get; set; } // "Static" or "Conditional"
        public bool Value { get; set; } // For "Static"
        public RequirementCondition Condition { get; set; } // For "Conditional"
    }

    public class RequirementCondition
    {
        public string Operator { get; set; } // "Equals", etc.
        public ConditionOperand Left { get; set; }
        public ConditionOperand Right { get; set; }
    }

    public class ConditionOperand
    {
        public string Source { get; set; } // "Parameter", "Constant"
        public string Name { get; set; } // If Source is Parameter
        public object Value { get; set; } // If Source is Constant (or Right side often)
    }

    public class NodeOutput
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public string Description { get; set; }
        public string ProducedOn { get; set; } // "Success", "Failure", "Event:MessageReceived"
    }

    public class ExecutionResult
    {
        [BsonId]
        public Guid Id { get; set; }
        public Guid ExecutionId { get; set; }
        public string NodeId { get; set; }
        public string Output { get; set; } // JSON serialized object for multiple outputs
        public DateTime CompletedAt { get; set; }
    }

    public class NodeInput
    {
        public string SourceNodeId { get; set; }
        public string SourceOutputName { get; set; }
    }

    // --- UI Metadata DTOs ---

    public class NodeMeta
    {
        public string NodeKey { get; set; }
        public string DisplayName { get; set; }
        public string NodeType { get; set; }
        public string NodeTypeKey { get; set; }
        public int NodeTypeOrder { get; set; }
        public string VariantSource { get; set; }
        public List<VariantMeta> Variants { get; set; }
    }

    public class VariantMeta
    {
        public string Value { get; set; }
        public string Label { get; set; }
        public List<NodeParameterMeta> Parameters { get; set; }
        public List<NodeOutput> Outputs { get; set; }
    }

    public class NodeParameterMeta
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string DataType { get; set; }
        public string Source { get; set; }
        public object Value { get; set; }
    }
}
