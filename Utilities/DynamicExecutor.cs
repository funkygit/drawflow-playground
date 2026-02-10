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
        object CreateInstance(string dllPath, string typeName);
        Task<object> ExecuteMethodAsync(object instance, MethodDefinition methodDef, Dictionary<string, object> inputs);
        Task<object> ExecuteAsync(NodeConfiguration config, Dictionary<string, object> inputs); // Legacy/Simple wrapper
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

        public object CreateInstance(string dllPath, string typeName)
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

            var type = assembly.GetType(typeName);
            if (type == null)
            {
                throw new TypeLoadException($"Type {typeName} not found in assembly {assemblyPath}");
            }

            return Activator.CreateInstance(type);
        }

        public async Task<object> ExecuteMethodAsync(object instance, MethodDefinition methodDef, Dictionary<string, object> inputs)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (methodDef == null) throw new ArgumentNullException(nameof(methodDef));

            var type = instance.GetType();
            var method = type.GetMethod(methodDef.MethodName);
            
            if (method == null)
            {
                throw new MissingMethodException($"Method {methodDef.MethodName} not found in type {type.FullName}");
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
                var resultProperty = task.GetType().GetProperty("Result");
                return resultProperty?.GetValue(task);
            }

            return result;
        }

        public async Task<object> ExecuteAsync(NodeConfiguration config, Dictionary<string, object> inputs)
        {
            // Wrapper for simple execution (Transient/Legacy)
            // If ExecutionFlow is present, run sequence
            if (config.ExecutionFlow != null && config.ExecutionFlow.Any())
            {
                var instance = CreateInstance(config.DllPath, config.TypeName);
                object lastResult = null;
                foreach (var method in config.ExecutionFlow.OrderBy(m => m.Sequence))
                {
                    lastResult = await ExecuteMethodAsync(instance, method, inputs);
                }
                return lastResult;
            }
            return null;
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
