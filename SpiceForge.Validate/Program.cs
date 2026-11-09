using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

const int SampleRate = 48000;
const int Oversample = 8;
const int Samples = 200;
const double Tolerance = 1e-6;

string repo = FindRepoRoot();
string testsDir = Path.Combine(repo, "Dependencies", "LiveSPICE", "Tests");
string? filter = args.Length > 0 ? args[0] : null;

var files = new[] { "Circuits", "Examples", "Unstable" }
    .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
    .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
    .ToList();

Console.WriteLine($"{"Circuit",-50}{"rows",5}{"y",5}{"q",5}{"k",4}{"src",5}{"qParam",7}{"residual",11}{"portErr",11}  status");
int failures = 0;
foreach (string file in files)
{
    string name = Path.GetRelativePath(testsDir, file).Replace('\\', '/').Replace(".schx", "");
    try
    {
        var circuit = Schematic.Load(file, new NullLog()).Build();
        ExtractedSystem system = Extractor.Extract(circuit.Analyze(), (Real)1 / (SampleRate * Oversample));

        double residual = ResidualError(system);
        double portError = PortError(system);
        int qWithParameters = system.Nonlinear.Count(q => system.Parameters.Any(p => q.DependsOn(p)));
        bool ok = residual <= Tolerance && portError <= Tolerance;
        if (!ok)
            failures++;
        Console.WriteLine($"{Truncate(name, 49),-50}{system.Rows,5}{system.Unknowns.Length,5}{system.Nonlinear.Length,5}{system.Ports,4}{system.Sources.Length,5}{qWithParameters,7}{residual,11:E1}{portError,11:E1}  {(ok ? "ok" : "FAIL")}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"{Truncate(name, 49),-50}error: {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine($"{files.Count} circuits, {failures} not passing.");
return failures == 0 ? 0 : 1;

// Largest relative difference between the original equations and E·y - H·h - C - sources - P·q.
static double ResidualError(ExtractedSystem s)
{
    Expression[] symbols = s.Unknowns.Concat(s.Parameters)
        .Concat(s.Equations.SelectMany(e => SymbolsOf(e)))
        .Distinct().ToArray();
    var expressions = s.Equations.Concat(s.Nonlinear).Concat(s.Sources.Select(x => x.Body)).ToArray();
    var evaluator = new ExpressionEvaluator(symbols, expressions);
    int[] unknownIndex = s.Unknowns.Select(u => Array.IndexOf(symbols, u)).ToArray();
    int[] parameterIndex = s.Parameters.Select(p => Array.IndexOf(symbols, p)).ToArray();

    var random = new Random(2);
    var values = new double[symbols.Length];
    var results = new double[expressions.Length];
    double worst = 0;

    for (int sample = 0; sample < Samples; sample++)
    {
        for (int i = 0; i < values.Length; i++)
            values[i] = random.NextDouble() * 2 - 1;
        evaluator.Evaluate(values, results);
        if (results.Any(r => double.IsNaN(r) || double.IsInfinity(r)))
            continue;

        for (int row = 0; row < s.Rows; row++)
        {
            double value = 0, scale = 0;
            void Add(double x) { value += x; scale += Math.Abs(x); }

            for (int j = 0; j < s.Unknowns.Length; j++)
                Add(s.E[row, j] * values[unknownIndex[j]]);
            for (int j = 0; j < s.Parameters.Length; j++)
                Add(-s.H[row, j] * values[parameterIndex[j]]);
            Add(-s.C[row]);
            for (int k = 0; k < s.Nonlinear.Length; k++)
                Add(-s.P[row, k] * results[s.Rows + k]);
            for (int x = 0; x < s.Sources.Length; x++)
                if (s.Sources[x].Row == row)
                    Add(-s.Sources[x].Coefficient * results[s.Rows + s.Nonlinear.Length + x]);

            double difference = Math.Abs(value - results[row]);
            worst = Math.Max(worst, difference / Math.Max(scale + Math.Abs(results[row]), 1e-12));
        }
    }
    return worst;
}

// Moving y along the null space of Q must leave every nonlinear term unchanged.
static double PortError(ExtractedSystem s)
{
    if (s.Nonlinear.Length == 0)
        return 0;

    Expression[] symbols = s.Unknowns.Concat(s.Parameters)
        .Concat(s.Nonlinear.SelectMany(q => SymbolsOf(q)))
        .Distinct().ToArray();
    var evaluator = new ExpressionEvaluator(symbols, s.Nonlinear);
    int n = s.Unknowns.Length;
    int[] unknownIndex = s.Unknowns.Select(u => Array.IndexOf(symbols, u)).ToArray();

    var random = new Random(3);
    var values = new double[symbols.Length];
    var moved = new double[symbols.Length];
    var before = new double[s.Nonlinear.Length];
    var after = new double[s.Nonlinear.Length];
    double worst = 0;

    for (int sample = 0; sample < Samples; sample++)
    {
        for (int i = 0; i < values.Length; i++)
            values[i] = random.NextDouble() * 2 - 1;

        var direction = new double[n];
        for (int j = 0; j < n; j++)
            direction[j] = random.NextDouble() * 2 - 1;
        ProjectOntoNullSpace(s.Q, direction);

        Array.Copy(values, moved, values.Length);
        for (int j = 0; j < n; j++)
            moved[unknownIndex[j]] += direction[j];

        evaluator.Evaluate(values, before);
        evaluator.Evaluate(moved, after);
        for (int k = 0; k < before.Length; k++)
        {
            if (double.IsNaN(before[k]) || double.IsInfinity(before[k]) || double.IsNaN(after[k]) || double.IsInfinity(after[k]))
                continue;
            worst = Math.Max(worst, Math.Abs(after[k] - before[k]) / Math.Max(Math.Abs(before[k]) + Math.Abs(after[k]), 1e-12));
        }
    }
    return worst;
}

// Q is in reduced row echelon form, so each row's pivot is the only nonzero in its column.
static void ProjectOntoNullSpace(double[,] q, double[] v)
{
    int k = q.GetLength(0), n = q.GetLength(1);
    for (int r = 0; r < k; r++)
    {
        int pivot = -1;
        for (int j = 0; j < n && pivot < 0; j++)
            if (q[r, j] == 1 && Enumerable.Range(0, k).All(i => i == r || q[i, j] == 0))
                pivot = j;

        double value = 0;
        for (int j = 0; j < n; j++)
            if (j != pivot)
                value += q[r, j] * v[j];
        v[pivot] = -value;
    }
}

static IEnumerable<Expression> SymbolsOf(Expression e) => SymbolScan.Collect(e);

static string Truncate(string s, int length) => s.Length <= length ? s : s.Substring(0, length);

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "SpiceForge.slnx")))
            return dir.FullName;
    throw new DirectoryNotFoundException("SpiceForge.slnx not found.");
}
