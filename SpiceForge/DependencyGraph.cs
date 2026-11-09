namespace SpiceForge;

/// <summary>Finds strongly connected components of a directed graph.</summary>
public static class DependencyGraph
{
    /// <summary>
    /// Returns the components of a graph in which edges point from a node to what it depends on.
    /// Each component appears after the components it depends on (Tarjan's algorithm, iterative).
    /// </summary>
    public static List<List<int>> StronglyConnected(int count, Func<int, IEnumerable<int>> dependencies)
    {
        var index = new int[count];
        var low = new int[count];
        var onStack = new bool[count];
        Array.Fill(index, -1);

        var components = new List<List<int>>();
        var stack = new Stack<int>();
        int next = 0;

        for (int root = 0; root < count; root++)
        {
            if (index[root] >= 0)
                continue;

            var work = new Stack<(int Node, IEnumerator<int> Edges)>();
            Open(root);

            while (work.Count > 0)
            {
                var (node, edges) = work.Peek();
                if (edges.MoveNext())
                {
                    int target = edges.Current;
                    if (index[target] < 0)
                        Open(target);
                    else if (onStack[target])
                        low[node] = Math.Min(low[node], index[target]);
                    continue;
                }

                work.Pop();
                edges.Dispose();
                if (work.Count > 0)
                {
                    int parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }

                if (low[node] == index[node])
                {
                    var component = new List<int>();
                    int member;
                    do
                    {
                        member = stack.Pop();
                        onStack[member] = false;
                        component.Add(member);
                    } while (member != node);
                    components.Add(component);
                }
            }

            void Open(int node)
            {
                index[node] = low[node] = next++;
                stack.Push(node);
                onStack[node] = true;
                work.Push((node, dependencies(node).ToList().GetEnumerator()));
            }
        }

        return components;
    }
}
