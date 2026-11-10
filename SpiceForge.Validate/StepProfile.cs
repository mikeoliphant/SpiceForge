using System.Linq.Expressions;
using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;
using LinqExpr = System.Linq.Expressions.Expression;

/// <summary>
/// Counts the operations in a circuit's compiled step function, baked-in against dynamic pots (with live
/// coefficients inlined, so only structure differs), split into the part before the Newton loop, the loop body,
/// and the part after it.
/// </summary>
internal static class StepProfile
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;

    public static int Run(string testsDir, string filter)
    {
        string file = new[] { "Circuits", "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx"))
            .First(p => p.Contains(filter, StringComparison.OrdinalIgnoreCase));
        Expression h = (Real)1 / (SampleRate * Oversample);

        LambdaExpression Compile(bool dynamic)
        {
            var baked = Schematic.Load(file, new NullLog()).Build();
            TransientSolution solution = TransientSolution.Solve(baked.Analyze(), h);
            var circuit = dynamic ? Schematic.Load(file, new NullLog()).Build() : baked;
            IReadOnlyList<PotParameter> pots = dynamic ? DynamicPots.Replace(circuit) : Array.Empty<PotParameter>();
            ExtractedSystem system = Extractor.Extract(circuit.Analyze(), h, pots);
            Expression input = circuit.Components.Where(i => i is Input).Select(i => Component.DependentVariable(i.Name, Component.t)).Single();
            Expression[] outputs = circuit.Nodes.Select(i => i.V).ToArray();
            // --speaker: only the first speaker's node, as LiveSPICE's own benchmark (and JitCheck) measures.
            if (Environment.GetCommandLineArgs().Contains("--speaker")
                && circuit.Components.OfType<Speaker>().FirstOrDefault()?.Anode.ConnectedTo?.V is Expression anode)
                outputs = new[] { anode };
            CompiledCircuit.InlineLiveForTesting = dynamic;
            _ = new CompiledCircuit(system, input, outputs, h, Oversample, IterationCap, solution);
            CompiledCircuit.InlineLiveForTesting = false;
            return CompiledCircuit.LastStepForTesting!;
        }

        // Baked and dynamic, both through the reduced path (as every circuit is), so both columns come from the same emitter.
        LambdaExpression bakedStep = Compile(false);
        LambdaExpression dynamicStep = Compile(true);
        var debugView = typeof(LinqExpr).GetProperty("DebugView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        string dumps = Environment.GetEnvironmentVariable("STEP_DUMP") ?? "";
        if (dumps.Length > 0)
        {
            File.WriteAllText(Path.Combine(dumps, "baked.txt"), (string)debugView.GetValue(bakedStep)!);
            File.WriteAllText(Path.Combine(dumps, "dynamic.txt"), (string)debugView.GetValue(dynamicStep)!);
        }
        var baked = Profile(bakedStep);
        var dynamic = Profile(dynamicStep);
        Console.WriteLine($"locals: baked {Locals(bakedStep)}, dynamic {Locals(dynamicStep)}; nodes: baked {Nodes(bakedStep)}, dynamic {Nodes(dynamicStep)}");
        Console.WriteLine($"{"section / node",-34}{"baked",10}{"dynamic",10}{"difference",12}");
        foreach (string section in new[] { "before loop", "loop body", "after loop" })
        {
            var keys = baked[section].Keys.Union(dynamic[section].Keys).OrderByDescending(key => Math.Abs(dynamic[section].GetValueOrDefault(key) - baked[section].GetValueOrDefault(key)));
            int totalBaked = baked[section].Values.Sum(), totalDynamic = dynamic[section].Values.Sum();
            Console.WriteLine($"{section,-34}{totalBaked,10}{totalDynamic,10}{totalDynamic - totalBaked,12}");
            foreach (string key in keys.Take(8))
            {
                int a = baked[section].GetValueOrDefault(key), b = dynamic[section].GetValueOrDefault(key);
                if (a != b)
                    Console.WriteLine($"  {key,-32}{a,10}{b,10}{b - a,12}");
            }
        }
        return 0;
    }

    // Node counts by kind, in three sections: everything before the first loop, the first loop's body, and after it.
    private static Dictionary<string, Dictionary<string, int>> Profile(LambdaExpression lambda)
    {
        var sections = new Dictionary<string, Dictionary<string, int>>
        {
            ["before loop"] = new(),
            ["loop body"] = new(),
            ["after loop"] = new(),
        };
        var body = (BlockExpression)lambda.Body;
        string section = "before loop";
        bool inLoop = false;
        foreach (LinqExpr statement in body.Expressions)
        {
            // CodeGen's DoWhile emits a label, the body, then a conditional jump back to that label.
            if (!inLoop && statement is LabelExpression label && label.Target.Name?.StartsWith("do_") == true && section == "before loop")
            {
                section = "loop body";
                inLoop = true;
                continue;
            }
            if (inLoop && statement is LabelExpression end && end.Target.Name?.StartsWith("do_") == true && end.Target.Name.EndsWith("_end"))
            {
                section = "after loop";
                inLoop = false;
                continue;
            }
            new Counter(sections[section]).Visit(statement);
        }
        return sections;
    }

    private static int Locals(LambdaExpression lambda)
    {
        var counter = new LocalCounter();
        counter.Visit(lambda.Body);
        return counter.Count;
    }

    private static int Nodes(LambdaExpression lambda)
    {
        var counts = new Dictionary<string, int>();
        new Counter(counts).Visit(lambda.Body);
        return counts.Values.Sum();
    }

    private sealed class LocalCounter : ExpressionVisitor
    {
        public int Count;
        protected override LinqExpr VisitBlock(BlockExpression node)
        {
            Count += node.Variables.Count;
            return base.VisitBlock(node);
        }
    }
    private sealed class Counter : ExpressionVisitor
    {
        private readonly Dictionary<string, int> counts;
        public Counter(Dictionary<string, int> counts) => this.counts = counts;

        public override LinqExpr? Visit(LinqExpr? node)
        {
            if (node != null)
            {
                string key = node is MethodCallExpression call ? "Call " + call.Method.Name
                    : node is BinaryExpression binary && binary.NodeType == ExpressionType.ArrayIndex ? "ArrayIndex"
                    : node.NodeType.ToString();
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            return base.Visit(node);
        }
    }
}
