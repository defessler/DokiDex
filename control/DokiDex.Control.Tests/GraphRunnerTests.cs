using System.Collections.Generic;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The execution core of node-lite flow: topological sort over the gen-step DAG. Pure, no GPU.
public class GraphRunnerTests
{
    private static GraphNode N(string id) => new(id, $"prompt {id}");

    [Fact]
    public void Linear_chain_runs_in_dependency_order()
    {
        var order = GraphRunner.ExecutionOrder(
            new[] { N("a"), N("b"), N("c") },
            new[] { new GraphEdge("a", "b"), new GraphEdge("b", "c") });
        Assert.Equal(new[] { "a", "b", "c" }, order);
    }

    [Fact]
    public void Diamond_dag_puts_the_root_first_and_the_join_last()
    {
        // a -> b, a -> c, b -> d, c -> d
        var order = GraphRunner.ExecutionOrder(
            new[] { N("a"), N("b"), N("c"), N("d") },
            new[] { new GraphEdge("a", "b"), new GraphEdge("a", "c"), new GraphEdge("b", "d"), new GraphEdge("c", "d") });
        Assert.NotNull(order);
        var o = order!.ToList();
        Assert.Equal("a", o[0]);
        Assert.Equal("d", o[^1]);
        Assert.True(o.IndexOf("b") < o.IndexOf("d") && o.IndexOf("c") < o.IndexOf("d"));
    }

    [Fact]
    public void A_cycle_returns_null()
        => Assert.Null(GraphRunner.ExecutionOrder(
            new[] { N("a"), N("b") },
            new[] { new GraphEdge("a", "b"), new GraphEdge("b", "a") }));

    [Fact]
    public void Disconnected_nodes_are_all_included()
    {
        var order = GraphRunner.ExecutionOrder(new[] { N("a"), N("b"), N("c") }, System.Array.Empty<GraphEdge>());
        Assert.Equal(3, order!.Count);
        Assert.Contains("a", order); Assert.Contains("b", order); Assert.Contains("c", order);
    }

    [Fact]
    public void Dangling_and_self_edges_are_ignored()
    {
        // edge to a missing node + a self-edge must not break the sort or create a phantom cycle
        var order = GraphRunner.ExecutionOrder(
            new[] { N("a"), N("b") },
            new[] { new GraphEdge("a", "b"), new GraphEdge("a", "ghost"), new GraphEdge("b", "b") });
        Assert.Equal(new[] { "a", "b" }, order);
    }
}
