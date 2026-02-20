using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DrawflowPlayground.Models;

namespace DrawflowPlayground.Utilities
{
    public class ParameterParser
    {
        public bool ValidateAndExtract(List<NodeParameter> configParams, Dictionary<string, object> uiInputs, out object[] methodArgs, out string error)
        {
            methodArgs = null;
            error = null;

            if (configParams == null || configParams.Count == 0)
            {
                methodArgs = Array.Empty<object>();
                return true;
            }

            var args = new object[configParams.Count];
            
            // Sort by Order
            var orderedParams = configParams.OrderBy(p => p.Order).ToList();

            for (int i = 0; i < orderedParams.Count; i++)
            {
                var param = orderedParams[i];
                object val = null;

                // 1. Retrieve Value
                if (param.Source == "Constant")
                {
                    val = param.Value;
                }
                else if (uiInputs.TryGetValue(param.Name, out var inputVal))
                {
                    val = inputVal;
                }

                // 2. Check Requirement (IsRequired)
                bool isRequired = ParseRequirement(param.IsRequired, uiInputs);

                if (isRequired && (val == null || (val is string s && string.IsNullOrWhiteSpace(s))))
                {
                    if (param.HasDefaultValue)
                    {
                        val = param.DefaultValue;
                    }
                    else
                    {
                        error = $"Missing required parameter: {param.Name}";
                        return false;
                    }
                }

                // 3. Convert Type
                if (val != null)
                {
                    try
                    {
                        // Handle JSON Element if coming from deserialized JSON
                        if (val is JsonElement je)
                        {
                            if (je.ValueKind == JsonValueKind.String) val = je.GetString();
                            else if (je.ValueKind == JsonValueKind.Number) val = je.GetInt32(); // Simplification
                            else if (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False) val = je.GetBoolean();
                        }

                        args[i] = Convert.ChangeType(val, param.ParameterType);
                    }
                    catch (Exception ex)
                    {
                        error = $"Invalid type for parameter {param.Name}. Expected {param.DataType}. Error: {ex.Message}";
                        return false;
                    }
                }
                else
                {
                     // Null allowed if not required
                     args[i] = GetDefault(param.ParameterType);
                }
            }

            methodArgs = args;
            return true;
        }

        private bool ParseRequirement(object isRequiredObj, Dictionary<string, object> uiInputs)
        {
            if (isRequiredObj == null) return false;

            // Handle boolean (Legacy or simple)
            if (isRequiredObj is bool b) return b;
            if (isRequiredObj.ToString().Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (isRequiredObj.ToString().Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            
            // Handle JObject/JsonElement (if deserialized as object)
            // Since we updated WorkflowEntities, we need to manually deserialize or cast if it comes as JsonElement
             try
            {
                // Assuming it might be deserialized to ParameterRequirement or JsonElement
                if (isRequiredObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
                {
                    // Manually parse JsonElement or use JsonSerializer
                    var req = JsonSerializer.Deserialize<ParameterRequirement>(je.GetRawText());
                     return EvaluateRequirement(req, uiInputs);
                }
                else if (isRequiredObj is ParameterRequirement req)
                {
                    return EvaluateRequirement(req, uiInputs);
                }
            }
            catch
            {
                // Fallback or log error
            }

            return false;
        }

        private bool EvaluateRequirement(ParameterRequirement req, Dictionary<string, object> uiInputs)
        {
            if (req == null) return false;
            if (req.Type == "Static") return req.Value;
            if (req.Type == "Conditional" && req.Condition != null)
            {
                return EvaluateCondition(req.Condition, uiInputs);
            }
            return false;
        }

        private bool EvaluateCondition(RequirementCondition cond, Dictionary<string, object> inputs)
        {
            // Resolve Left
            object leftVal = ResolveOperand(cond.Left, inputs);
            object rightVal = ResolveOperand(cond.Right, inputs);

            // Compare
            if (cond.Operator == "Equals")
            {
                return Equals(leftVal?.ToString(), rightVal?.ToString());
            }
            // Add more operators as needed

            return false;
        }

        private object ResolveOperand(ConditionOperand op, Dictionary<string, object> inputs)
        {
            if (op == null) return null;
            if (op.Source == "Constant") return op.Value;
            if (op.Source == "Parameter")
            {
                if (inputs.TryGetValue(op.Name, out var val))
                {
                     if (val is JsonElement je) return je.ToString();
                     return val;
                }
                return null;
            }
            return null; // Value?
        }
        
        private object GetDefault(Type type)
        {
            if (type.IsValueType) return Activator.CreateInstance(type);
            return null;
        }
    }
}
