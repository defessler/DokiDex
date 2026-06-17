using System.Collections.Generic;
using System.Linq;

namespace DokiDex.Web;

// One node in a node-lite flow: a gen step (a prompt + kind + the common knobs), with an id used by edges.
public sealed record GraphNode(string Id, string? Prompt, string? Kind = "image", bool Fast = false,
    int Seed = -1, string? Aspect = null, string? Lora = null, string? Negative = null);

// A dependency edge: From must run before To.
public sealed record GraphEdge(string From, string To);

// The graph a "Flow" surface posts: nodes + dependency edges.
public sealed record GraphSpec(List<GraphNode>? Nodes, List<GraphEdge>? Edges);

// The execution core of node-lite flow: a topological sort over the gen-step DAG. Pure + total -> unit-tested;
// the endpoint then submits the nodes in this order through the existing single-flight queue. Detects cycles
// (returns null) so a bad graph fails cleanly instead of deadlocking.
public static class GraphRunner
{
    // Kahn's algorithm. Returns node ids in an order where every dependency precedes its dependents, or null
    // if the graph has a cycle (or an edge references a missing node and that leaves a cycle). Nodes with no
    // edges are included (any order). Stable: ties break by the nodes' input order.
    public static IReadOnlyList<string>? ExecutionOrder(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        var ids = nodes.Select(n => n.Id).ToList();
        var idSet = new HashSet<string>(ids);
        var indeg = ids.ToDictionary(i => i, _ => 0);
        var outs = ids.ToDictionary(i => i, _ => new List<string>());
        foreach (var e in edges)
        {
            if (!idSet.Contains(e.From) || !idSet.Contains(e.To) || e.From == e.To) continue;   // ignore dangling/self
            outs[e.From].Add(e.To);
            indeg[e.To]++;
        }
        // seed the ready set in input order (stable), then drain
        var ready = new Queue<string>(ids.Where(i => indeg[i] == 0));
        var order = new List<string>(ids.Count);
        while (ready.Count > 0)
        {
            var n = ready.Dequeue();
            order.Add(n);
            foreach (var m in outs[n])
                if (--indeg[m] == 0) ready.Enqueue(m);
        }
        return order.Count == ids.Count ? order : null;   // fewer than all -> a cycle remains
    }
}
