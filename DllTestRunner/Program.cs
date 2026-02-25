using System.Text.Json;
using DrawflowPlayground.Models;
using DrawflowPlayground.Utilities;
using Microsoft.Extensions.Logging;

namespace DllTestRunner;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║        DLL Test Runner v1.0          ║");
        Console.WriteLine("║   DynamicExecutor Console Harness    ║");
        Console.WriteLine("╚══════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();

        // --- Parse arguments ---
        var configPath = GetArgValue(args, "--config") ?? "test-config.json";
        var targetNode = GetArgValue(args, "--node");
        var paramString = GetArgValue(args, "--params");
        var autoMode = args.Contains("--auto");
        var variantValue = GetArgValue(args, "--variant");

        // --- Load configuration ---
        if (!File.Exists(configPath))
        {
            WriteError($"Config file not found: {configPath}");
            return 1;
        }

        Console.WriteLine($"  Config: {Path.GetFullPath(configPath)}");
        Console.WriteLine();

        List<NodeConfiguration> configs;
        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            configs = JsonSerializer.Deserialize<List<NodeConfiguration>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<NodeConfiguration>();
        }
        catch (Exception ex)
        {
            WriteError($"Failed to parse config: {ex.Message}");
            return 1;
        }

        if (configs.Count == 0)
        {
            WriteError("No node configurations found in the config file.");
            return 1;
        }

        // --- Select node ---
        NodeConfiguration selectedConfig;
        if (!string.IsNullOrEmpty(targetNode))
        {
            selectedConfig = configs.FirstOrDefault(c =>
                c.NodeKey.Equals(targetNode, StringComparison.OrdinalIgnoreCase))!;
            if (selectedConfig == null)
            {
                WriteError($"Node '{targetNode}' not found. Available: {string.Join(", ", configs.Select(c => c.NodeKey))}");
                return 1;
            }
        }
        else
        {
            selectedConfig = PickNode(configs);
        }

        WriteHeader($"Selected: {selectedConfig.DisplayName} ({selectedConfig.NodeKey})");
        Console.WriteLine($"  DLL Path     : {selectedConfig.DllPath}");
        Console.WriteLine($"  Exec Mode    : {selectedConfig.ExecutionMode}");

        // --- Select variant ---
        NodeVariant? selectedVariant = null;
        if (selectedConfig.Variants != null && selectedConfig.Variants.Count > 0)
        {
            if (!string.IsNullOrEmpty(variantValue))
            {
                selectedVariant = selectedConfig.Variants.FirstOrDefault(v =>
                    v.Value.Equals(variantValue, StringComparison.OrdinalIgnoreCase));
            }

            if (selectedVariant == null && selectedConfig.Variants.Count == 1)
            {
                selectedVariant = selectedConfig.Variants[0];
            }
            else if (selectedVariant == null)
            {
                selectedVariant = PickVariant(selectedConfig.Variants);
            }

            Console.WriteLine($"  Variant      : {selectedVariant.Label} ({selectedVariant.Value})");
            Console.WriteLine($"  TypeName     : {selectedVariant.TypeName}");
        }

        Console.WriteLine();

        // --- Resolve effective config from variant ---
        var typeName = selectedVariant?.TypeName;
        var constructorDef = selectedVariant?.Constructor;
        var executionFlow = selectedVariant?.ExecutionFlow ?? selectedConfig.ExecutionFlow;

        if (string.IsNullOrEmpty(typeName))
        {
            WriteError("No TypeName found in the variant. Cannot proceed.");
            return 1;
        }

        // --- Collect parameters ---
        var inputs = new Dictionary<string, object>();

        // Parse --params if in auto mode
        if (autoMode && !string.IsNullOrEmpty(paramString))
        {
            foreach (var pair in paramString.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2)
                {
                    inputs[kv[0].Trim()] = kv[1].Trim();
                }
            }
        }

        // Prompt for constructor params if interactive
        if (constructorDef?.Parameters != null)
        {
            WriteHeader("Constructor Parameters");
            foreach (var param in constructorDef.Parameters.OrderBy(p => p.Order))
            {
                if (!inputs.ContainsKey(param.Name))
                {
                    if (autoMode)
                    {
                        // Use default from config
                        inputs[param.Name] = param.Value ?? param.DefaultValue ?? GetTypeDefault(param.DataType);
                        Console.WriteLine($"  {param.DisplayName ?? param.Name} = {inputs[param.Name]} (default)");
                    }
                    else
                    {
                        inputs[param.Name] = PromptForParameter(param);
                    }
                }
                else
                {
                    Console.WriteLine($"  {param.DisplayName ?? param.Name} = {inputs[param.Name]} (from --params)");
                }
            }
            Console.WriteLine();
        }

        // Collect method params
        if (executionFlow != null)
        {
            foreach (var method in executionFlow.OrderBy(m => m.Sequence))
            {
                if (method.Parameters != null && method.Parameters.Count > 0)
                {
                    WriteHeader($"Parameters for {method.MethodName}()");
                    foreach (var param in method.Parameters.OrderBy(p => p.Order))
                    {
                        if (!inputs.ContainsKey(param.Name))
                        {
                            if (autoMode)
                            {
                                inputs[param.Name] = param.Value ?? param.DefaultValue ?? GetTypeDefault(param.DataType);
                                Console.WriteLine($"  {param.DisplayName ?? param.Name} = {inputs[param.Name]} (default)");
                            }
                            else
                            {
                                inputs[param.Name] = PromptForParameter(param);
                            }
                        }
                    }
                    Console.WriteLine();
                }
            }
        }

        // --- Resolve DLL path ---
        var dllPath = selectedConfig.DllPath;
        if (!Path.IsPathRooted(dllPath))
        {
            dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dllPath);
        }

        if (!File.Exists(dllPath))
        {
            WriteError($"DLL not found at: {dllPath}");
            WriteInfo("Hint: Make sure the DLL is in the output directory or provide an absolute path in the config.");
            return 1;
        }

        // --- Execute ---
        WriteHeader("Execution");
        Console.WriteLine($"  Loading assembly: {dllPath}");
        Console.WriteLine();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Warning);
        });
        var logger = loggerFactory.CreateLogger<DynamicExecutor>();
        var executor = new DynamicExecutor(logger);

        try
        {
            // Create instance
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  → Creating instance of {typeName}...");
            Console.ResetColor();

            var instance = executor.CreateInstance(
                selectedConfig.DllPath,
                typeName,
                constructorDef,
                inputs);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✓ Instance created: {instance.GetType().FullName}");
            Console.ResetColor();
            Console.WriteLine();

            // Execute methods
            if (executionFlow != null && executionFlow.Count > 0)
            {
                object? lastResult = null;
                foreach (var method in executionFlow.OrderBy(m => m.Sequence))
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  → Executing {method.MethodName}() [Step {method.Sequence}]...");
                    Console.ResetColor();

                    lastResult = await executor.ExecuteMethodAsync(instance, method, inputs);

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write($"  ✓ {method.MethodName}() returned: ");
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(FormatResult(lastResult));
                    Console.ResetColor();
                    Console.WriteLine();
                }

                // Summary
                WriteHeader("Result Summary");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  Final result: {FormatResult(lastResult)}");
                Console.ResetColor();
            }
            else
            {
                WriteInfo("No ExecutionFlow defined. Instance was created successfully but no methods were called.");
            }

            // Dispose if needed
            if (instance is IDisposable disposable)
            {
                disposable.Dispose();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  Instance disposed.");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            WriteError($"Execution failed: {ex.Message}");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n  Stack trace:\n{ex.StackTrace}");
            Console.ResetColor();

            if (ex.InnerException != null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n  Inner exception: {ex.InnerException.Message}");
                Console.ResetColor();
            }

            return 1;
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  Done! ✓");
        Console.ResetColor();
        return 0;
    }

    // ─── Helpers ─────────────────────────────────────────────────

    static NodeConfiguration PickNode(List<NodeConfiguration> configs)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  Available nodes:");
        Console.ResetColor();
        for (int i = 0; i < configs.Count; i++)
        {
            var c = configs[i];
            Console.WriteLine($"    [{i + 1}] {c.DisplayName} ({c.NodeKey}) - {c.ExecutionMode}");
        }
        Console.WriteLine();

        while (true)
        {
            Console.Write("  Select a node [1-" + configs.Count + "]: ");
            if (int.TryParse(Console.ReadLine(), out var idx) && idx >= 1 && idx <= configs.Count)
            {
                Console.WriteLine();
                return configs[idx - 1];
            }
            Console.WriteLine("  Invalid selection, try again.");
        }
    }

    static NodeVariant PickVariant(List<NodeVariant> variants)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  Available variants:");
        Console.ResetColor();
        for (int i = 0; i < variants.Count; i++)
        {
            Console.WriteLine($"    [{i + 1}] {variants[i].Label} ({variants[i].Value})");
        }
        Console.WriteLine();

        while (true)
        {
            Console.Write("  Select a variant [1-" + variants.Count + "]: ");
            if (int.TryParse(Console.ReadLine(), out var idx) && idx >= 1 && idx <= variants.Count)
            {
                return variants[idx - 1];
            }
            Console.WriteLine("  Invalid selection, try again.");
        }
    }

    static object PromptForParameter(NodeParameter param)
    {
        var defaultDisplay = param.Value != null ? $" (default: {param.Value})" : "";
        var allowedDisplay = param.AllowedValues != null
            ? $" [{string.Join("/", param.AllowedValues)}]"
            : "";

        Console.Write($"  {param.DisplayName ?? param.Name} ({param.DataType}){allowedDisplay}{defaultDisplay}: ");
        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input) && param.Value != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"    → Using default: {param.Value}");
            Console.ResetColor();
            return param.Value;
        }

        return input ?? "";
    }

    static string FormatResult(object? result)
    {
        if (result == null) return "(null)";
        try
        {
            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
        }
        catch
        {
            return result.ToString() ?? "(null)";
        }
    }

    static object GetTypeDefault(string dataType)
    {
        return dataType switch
        {
            "System.Int32" => 0,
            "System.Boolean" => false,
            "System.Double" => 0.0,
            _ => ""
        };
    }

    static string? GetArgValue(string[] args, string key)
    {
        var idx = Array.IndexOf(args, key);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static void WriteHeader(string text)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"  ── {text} ──");
        Console.ResetColor();
    }

    static void WriteError(string text)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ✗ {text}");
        Console.ResetColor();
    }

    static void WriteInfo(string text)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"  ℹ {text}");
        Console.ResetColor();
    }
}
