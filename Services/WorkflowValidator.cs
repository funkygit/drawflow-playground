using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DrawflowPlayground.Models;
using Microsoft.Extensions.Logging;

namespace DrawflowPlayground.Services
{
    public class WorkflowValidator
    {
        private readonly ILogger<WorkflowValidator> _logger;

        public WorkflowValidator(ILogger<WorkflowValidator> logger)
        {
            _logger = logger;
        }

        public List<string> Validate(WorkflowDefinition workflow, List<NodeConfiguration> allNodeConfigs)
        {
            var errors = new List<string>();

            if (string.IsNullOrEmpty(workflow.JsonData))
            {
                errors.Add("Workflow definition is empty.");
                return errors;
            }

            try
            {
                var jsonNode = JsonNode.Parse(workflow.JsonData);
                // Assumption: Simplified traversal or we need a helper to extract nodes from Drawflow JSON structure.
                // For this implementation, I will assume we can iterate "Home" -> "data" and get nodes.
                // Drawflow: { "drawflow": { "Home": { "data": { "1": { "id": 1, "name": "tcp_server", "data": { ... } } } } } }

                var nodes = jsonNode?["drawflow"]?["Home"]?["data"]?.AsObject();
                if (nodes == null) return errors;

                // 1. Build Output Map: NodeId -> List<NodeOutput>
                var workflowOutputs = new Dictionary<string, List<NodeOutput>>();
                
                // First pass: Register Outputs
                foreach (var kvp in nodes)
                {
                    var nodeId = kvp.Key;
                    var nodeObj = kvp.Value;
                    var nodeName = nodeObj?["name"]?.ToString();
                    var nodeKey = nodeObj?["data"]?["NodeKey"]?.ToString(); // Assuming data stores NodeKey or we map name->key
                    
                    if (string.IsNullOrEmpty(nodeKey)) continue; // Skip unknown

                    var config = allNodeConfigs.FirstOrDefault(c => c.NodeKey == nodeKey);
                    if (config != null && config.Outputs != null)
                    {
                        workflowOutputs[nodeId] = config.Outputs;
                    }
                }

                // Second pass: Validate Inputs
                foreach (var kvp in nodes)
                {
                    var nodeId = kvp.Key;
                    var nodeObj = kvp.Value;
                    var nodeKey = nodeObj?["data"]?["NodeKey"]?.ToString();
                    
                    if (string.IsNullOrEmpty(nodeKey)) continue;

                    var config = allNodeConfigs.FirstOrDefault(c => c.NodeKey == nodeKey);
                    if (config == null) continue;

                    // Iterate through Expected Parameters
                    // We need to check the "data" object of the node to see what values are set.
                    var nodeData = nodeObj?["data"];
                    
                    var allParams = new List<NodeParameter>();
                    if (config.Lifecycle?.OnStart?.Parameters != null) allParams.AddRange(config.Lifecycle.OnStart.Parameters);
                    if (config.ExecutionFlow != null)
                    {
                        foreach(var method in config.ExecutionFlow)
                        {
                            if (method.Parameters != null) allParams.AddRange(method.Parameters);
                        }
                    }

                    foreach (var param in allParams)
                    {
                         // Check if this parameter is configured as a NodeInput
                         var paramValue = nodeData?[param.Name];
                         
                         // Logic to detect if it's a NodeInput.
                         // 1. Explicit Source="NodeOutput"? 
                         // 2. Or structure of value is { SourceNodeId, SourceOutputName }?
                         // Let's assume the UI saves it as an object if it's a link.
                         
                         if (paramValue is JsonObject jo && jo.ContainsKey("SourceNodeId") && jo.ContainsKey("SourceOutputName"))
                         {
                             var input = JsonSerializer.Deserialize<NodeInput>(jo.ToJsonString());
                             
                             if (input != null)
                             {
                                 // Validate connection
                                 if (!workflowOutputs.TryGetValue(input.SourceNodeId, out var outputs))
                                 {
                                     errors.Add($"Node {nodeId}: Parameter '{param.Name}' references missing node {input.SourceNodeId}.");
                                     continue;
                                 }

                                 var sourceOutput = outputs.FirstOrDefault(o => o.Name == input.SourceOutputName);
                                 if (sourceOutput == null)
                                 {
                                     errors.Add($"Node {nodeId}: Parameter '{param.Name}' references missing output '{input.SourceOutputName}' on node {input.SourceNodeId}.");
                                     continue;
                                 }

                                 // Type Check
                                 if (sourceOutput.DataType != param.DataType && param.DataType != "System.Object")
                                 {
                                     errors.Add($"Node {nodeId}: Validation Error - Parameter '{param.Name}' expects {param.DataType} but '{input.SourceOutputName}' outputs {sourceOutput.DataType}.");
                                 }
                             }
                         }
                    }
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Validation failed.");
                errors.Add($"Validation Exception: {ex.Message}");
            }

            return errors;
        }
    }
}
