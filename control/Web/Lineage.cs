using System.Collections.Generic;
using System.Linq;

namespace DokiDex.Web;

// One library artifact as the lineage builder sees it: its name, the source it was derived from (or null for
// an original), and a label/kind for display.
public sealed record LinItem(string Name, string? Parent, string Prompt, string Kind);

// A node in the variation-lineage forest: an artifact plus the artifacts derived FROM it (refine/effect/vary).
public sealed record LinNode(string Name, string Prompt, string Kind, List<LinNode> Children);

// The variation tree (Runway "Sessions" / Ideogram boards): generations link to the card they were derived
// from via the sidecar Parent, forming a forest. BuildForest is pure + total -> unit-tested:
//   - originals (no parent, or a parent no longer in the set) become roots;
//   - children attach under their parent, preserving input order (caller sorts: newest-first);
//   - duplicate names keep the first; self-parent and parent-cycles can't strand a node (it falls back to root).
public static class Lineage
{
    public static List<LinNode> BuildForest(IEnumerable<LinItem> items)
    {
        var list = items?.ToList() ?? new();
        var nodes = new Dictionary<string, LinNode>();
        foreach (var i in list)
            if (i is not null && !string.IsNullOrEmpty(i.Name) && !nodes.ContainsKey(i.Name))
                nodes[i.Name] = new LinNode(i.Name, i.Prompt ?? "", i.Kind ?? "", new());

        var roots = new List<LinNode>();
        var attached = new HashSet<string>();
        foreach (var i in list)
        {
            if (i is null || !nodes.TryGetValue(i.Name, out var node) || attached.Contains(i.Name)) continue;
            attached.Add(i.Name);
            if (i.Parent is not null && i.Parent != i.Name
                && nodes.TryGetValue(i.Parent, out var parent)
                && !IsAncestor(node, parent))                 // guard: don't let a cycle strand the node
                parent.Children.Add(node);
            else
                roots.Add(node);
        }
        return roots;
    }

    // Would attaching `node` under `candidate` create a loop? True if `node` is `candidate` or already an
    // ancestor of `candidate` (walking candidate's existing subtree, which is acyclic by construction).
    private static bool IsAncestor(LinNode node, LinNode candidate)
    {
        if (ReferenceEquals(node, candidate)) return true;
        foreach (var c in node.Children)
            if (IsAncestor(c, candidate)) return true;
        return false;
    }
}
