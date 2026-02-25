using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using DrawflowPlayground.Models;
using Microsoft.Extensions.Logging;

namespace DrawflowPlayground.Utilities
{
    public interface IDynamicExecutor
    {
        object CreateInstance(string dllPath, string typeName, MethodDefinition constructorDef = null, Dictionary<string, object> inputs = null);
        Task<object> ExecuteMethodAsync(object instance, MethodDefinition methodDef, Dictionary<string, object> inputs);
        Task<object> ExecuteAsync(NodeConfiguration config, string typeName, MethodDefinition constructorDef, Dictionary<string, object> inputs); // Legacy/Simple wrapper
        string ResolvePlaceholders(string template, Dictionary<string, object> inputs);
    }

    public class DynamicExecutor : IDynamicExecutor
    {
        private readonly ILogger<DynamicExecutor> _logger;
        private readonly ParameterParser _parser;

        public DynamicExecutor(ILogger<DynamicExecutor> logger)
        {
            _logger = logger;
            _parser = new ParameterParser();
        }

        public object CreateInstance(string dllPath, string typeName, MethodDefinition constructorDef = null, Dictionary<string, object> inputs = null)
        {
            var assemblyPath = Path.IsPathRooted(dllPath)
                ? dllPath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dllPath);

            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"Assembly not found at {assemblyPath}");
            }

            var loadContext = AssemblyLoadContext.Default; 
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

            var resolvedTypeName = ResolvePlaceholders(typeName, inputs ?? new Dictionary<string, object>());
            var type = assembly.GetType(resolvedTypeName);
            if (type == null)
            {
                throw new TypeLoadException($"Type {resolvedTypeName} not found in assembly {assemblyPath}");
            }

            if (constructorDef != null && inputs != null)
            {
                if (!_parser.ValidateAndExtract(constructorDef.Parameters, inputs, out var args, out var error))
                {
                    throw new ArgumentException($"Constructor parameter validation failed: {error}");
                }
                return Activator.CreateInstance(type, args);
            }

            return Activator.CreateInstance(type);
        }

        public async Task<object> ExecuteMethodAsync(object instance, MethodDefinition methodDef, Dictionary<string, object> inputs)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (methodDef == null) throw new ArgumentNullException(nameof(methodDef));

            var type = instance.GetType();
            var resolvedMethodName = ResolvePlaceholders(methodDef.MethodName, inputs);
            var method = type.GetMethod(resolvedMethodName);
            
            if (method == null)
            {
                throw new MissingMethodException($"Method {resolvedMethodName} not found in type {type.FullName}");
            }

            // Parse Parameters using Utility
            if (!_parser.ValidateAndExtract(methodDef.Parameters, inputs, out var args, out var error))
            {
                throw new ArgumentException($"Parameter validation failed: {error}");
            }

            // Invoke
            var result = method.Invoke(instance, args);

            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                var resultType = task.GetType();
                if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var resultProperty = resultType.GetProperty("Result");
                    return resultProperty?.GetValue(task);
                }
                return null;
            }

            return result;
        }

        public async Task<object> ExecuteAsync(NodeConfiguration config, string typeName, MethodDefinition constructorDef, Dictionary<string, object> inputs)
        {
            // Wrapper for simple execution (Transient/Legacy)
            // If ExecutionFlow is present, run sequence
            if (config.ExecutionFlow != null && config.ExecutionFlow.Any())
            {
                var instance = CreateInstance(config.DllPath, typeName, constructorDef, inputs);
                object lastResult = null;
                foreach (var method in config.ExecutionFlow.OrderBy(m => m.Sequence))
                {
                    lastResult = await ExecuteMethodAsync(instance, method, inputs);
                }
                if (instance is IDisposable d) d.Dispose();
                return lastResult;
            }
            return null;
        }

        public string ResolvePlaceholders(string template, Dictionary<string, object> inputs)
        {
            if (string.IsNullOrEmpty(template) || inputs == null) return template;

            var result = template;
            foreach (var input in inputs)
            {
                var placeholder = "{{" + input.Key + "}}";
                if (result.Contains(placeholder))
                {
                    result = result.Replace(placeholder, input.Value?.ToString() ?? "");
                }
            }
            return result;
        }

        private static object GetDefault(Type type)
        {
            if (type.IsValueType)
            {
                return Activator.CreateInstance(type);
            }
            return null;
        }
    }
}
