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

    /// <summary>
    /// Custom AssemblyLoadContext that resolves dependencies from the same directory
    /// as the plugin DLL. This is essential when loading assemblies with external dependencies.
    /// </summary>
    internal class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _pluginDirectory;

        public PluginLoadContext(string pluginPath) : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
            _pluginDirectory = Path.GetDirectoryName(pluginPath)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // First, try the deps.json-based resolver
            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            // Fallback: probe the plugin directory for the DLL by name
            var candidatePath = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(candidatePath))
            {
                return LoadFromAssemblyPath(candidatePath);
            }

            // Let the default context try (shared framework assemblies, etc.)
            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }
            return IntPtr.Zero;
        }
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

            assemblyPath = Path.GetFullPath(assemblyPath);

            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"Assembly not found at {assemblyPath}");
            }

            _logger.LogInformation("Loading assembly from: {Path}", assemblyPath);

            // Use a custom AssemblyLoadContext that can resolve dependencies
            // from the same directory as the target DLL
            var loadContext = new PluginLoadContext(assemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

            _logger.LogInformation("Assembly loaded: {Name} (v{Version})",
                assembly.GetName().Name, assembly.GetName().Version);

            var resolvedTypeName = ResolvePlaceholders(typeName, inputs ?? new Dictionary<string, object>());
            var type = assembly.GetType(resolvedTypeName);
            if (type == null)
            {
                // Diagnostic: list all available types to help debug
                var exportedTypes = new List<string>();
                try
                {
                    exportedTypes = assembly.GetExportedTypes()
                        .Select(t => t.FullName!)
                        .OrderBy(t => t)
                        .ToList();
                }
                catch (ReflectionTypeLoadException rtle)
                {
                    // Some types may fail to load due to missing dependencies
                    exportedTypes = rtle.Types
                        .Where(t => t != null)
                        .Select(t => t!.FullName!)
                        .OrderBy(t => t)
                        .ToList();

                    _logger.LogWarning("Some types could not be loaded. Loader exceptions:");
                    foreach (var ex in rtle.LoaderExceptions.Where(e => e != null).Take(10))
                    {
                        _logger.LogWarning("  - {Message}", ex!.Message);
                    }
                }

                var typeList = exportedTypes.Count > 0
                    ? string.Join("\n  - ", exportedTypes)
                    : "(none found — dependencies may be missing)";

                throw new TypeLoadException(
                    $"Type '{resolvedTypeName}' not found in assembly '{Path.GetFileName(assemblyPath)}'.\n" +
                    $"Available types:\n  - {typeList}");
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

