namespace SampleDll;

/// <summary>
/// A simple calculator class for testing DynamicExecutor.
/// Constructor accepts two integers, then methods operate on them.
/// </summary>
public class Calculator
{
    private readonly int _a;
    private readonly int _b;

    public Calculator(int a, int b)
    {
        _a = a;
        _b = b;
    }

    public int Add()
    {
        Console.WriteLine($"  [Calculator] Adding {_a} + {_b}");
        return _a + _b;
    }

    public int Multiply()
    {
        Console.WriteLine($"  [Calculator] Multiplying {_a} * {_b}");
        return _a * _b;
    }

    public int Subtract()
    {
        Console.WriteLine($"  [Calculator] Subtracting {_a} - {_b}");
        return _a - _b;
    }
}
