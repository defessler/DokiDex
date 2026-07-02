using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DokiDex.Web;

// Permission rules (1.5): CC-style persisted allow/deny rules, replacing the old flat in-memory per-tool "always
// allowed" HashSet<string> (which approved EVERY future call to that tool for the rest of the process the moment
// the user pressed 'a' once, and forgot everything the instant the process exited). A rule is a plain string:
// bare `Tool` (tool-wide — "Read", "Edit") or `Tool(specifier)` where the specifier is either an EXACT string or a
// trailing-`" *"` PREFIX rule — "Bash(git status)" matches only that literal command; "Bash(dotnet test *)"
// matches any command starting "dotnet test ". Tool names compare case-insensitively; specifiers compare
// case-sensitive-exact (or as a literal prefix, for the " *" form). Persisted at
// %USERPROFILE%\.doki\permissions\<workspace-hash>.json — ONE file per workspace (reuses CodeSessions.Hash, so
// the SAME per-workspace identity sessions already use) — as `{ "allow": [...], "deny": [...] }`.
//
// THE LOAD-BEARING SECURITY PROPERTY (Decide): deny is checked FIRST and wins over any allow, and a caller (see
// Program.cs's ApproveGated) uses a Deny verdict to skip the interactive y/a/n prompt ENTIRELY — a denied action
// never even asks. A malformed rule (bad syntax) is simply INERT (Matches returns false for it) rather than
// thrown/crashed on, so a typo in the persisted file can never take down permission checking.
//
// internal (not public): InternalsVisibleTo grants DokiDex.Cli (assembly "doki-code", Program.cs) and
// DokiDex.Control.Tests access — the exact same seam CodeSessions/CodeAgent.RunBash already use.
internal static class CodePermissions
{
    public enum Decision { Allow, Deny, Ask }

    // The persisted shape — property names are lowercase via JsonPropertyName to match the spec'd file format
    // exactly (`{"allow":[...],"deny":[...]}`) while keeping idiomatic PascalCase C# at every call site.
    public sealed record Rules(
        [property: JsonPropertyName("allow")] List<string> Allow,
        [property: JsonPropertyName("deny")] List<string> Deny)
    {
        public static Rules Empty => new(new List<string>(), new List<string>());
    }

    // ---- rule parsing (pure, total) ----

    // Parse a rule string into (tool, specifier). specifier is null for a bare tool-wide rule ("Read", "Edit").
    // Malformed rules — no closing paren, an empty tool name, or a stray/nested paren inside the specifier —
    // return false so Matches can treat them as inert rather than throwing.
    internal static bool TryParse(string rule, out string tool, out string? specifier)
    {
        tool = "";
        specifier = null;
        if (string.IsNullOrWhiteSpace(rule)) return false;
        var r = rule.Trim();
        var paren = r.IndexOf('(');
        if (paren < 0)
        {
            if (r.Contains(')')) return false;   // stray close-paren with no open — malformed
            tool = r;
            return tool.Length > 0;
        }
        if (!r.EndsWith(")", StringComparison.Ordinal)) return false;   // must close at the very end, no trailer
        tool = r[..paren].Trim();
        var inner = r[(paren + 1)..^1];
        if (tool.Length == 0 || inner.Contains('(') || inner.Contains(')')) return false;   // nested/stray parens
        specifier = inner;
        return true;
    }

    // PURE: is `rule` syntactically valid at all? Program.cs's `/permissions allow|deny <rule>` rejects a typo
    // before it's persisted as a permanently-inert rule.
    internal static bool IsValidRule(string rule) => TryParse(rule, out _, out _);

    // PURE: does `rule` govern this exact tool call? Tool-name compare is case-insensitive ("bash" in the rule
    // matches a "Bash" call); specifier compare is case-SENSITIVE-exact, or — when the rule's specifier ends
    // " *" — a whole-token PREFIX match requiring the literal "prefix + ' '" substring, so "Bash(git diff *)"
    // does NOT match "git diff-index" (the character right after "git diff" is '-', not the required space).
    internal static bool Matches(string rule, string tool, string specifier)
    {
        if (!TryParse(rule, out var ruleTool, out var ruleSpec)) return false;
        if (!string.Equals(ruleTool, tool, StringComparison.OrdinalIgnoreCase)) return false;
        if (ruleSpec is null) return true;   // bare tool-wide rule — any specifier matches
        if (ruleSpec.EndsWith(" *", StringComparison.Ordinal))
        {
            var prefix = ruleSpec[..^2];
            return (specifier ?? "").StartsWith(prefix + " ", StringComparison.Ordinal);
        }
        return string.Equals(ruleSpec, specifier, StringComparison.Ordinal);
    }

    // ---- decision (pure): deny checked FIRST and wins over any allow; else Ask ----

    internal static Decision Decide(Rules rules, string tool, string specifier)
    {
        if (rules.Deny.Any(r => Matches(r, tool, specifier))) return Decision.Deny;
        if (rules.Allow.Any(r => Matches(r, tool, specifier))) return Decision.Allow;
        return Decision.Ask;
    }

    // The first rule (in list order) that matches this call — Program.cs surfaces this in its "denied by
    // permission rule <rule>" message so the user sees WHICH rule fired, not just that one did.
    internal static string? FindMatchingRule(IEnumerable<string> ruleList, string tool, string specifier)
        => ruleList.FirstOrDefault(r => Matches(r, tool, specifier));

    // ---- editing (pure) ----

    internal static Rules AddAllow(Rules rules, string rule) => rules with { Allow = Appended(rules.Allow, rule) };
    internal static Rules AddDeny(Rules rules, string rule) => rules with { Deny = Appended(rules.Deny, rule) };

    private static List<string> Appended(List<string> list, string rule)
    {
        var l = new List<string>(list);
        if (!l.Contains(rule, StringComparer.Ordinal)) l.Add(rule);
        return l;
    }

    // One line of a numbered `/permissions` listing: allow rules first, then deny — numbered together (1..N) so
    // "/permissions remove <n>" addresses either list by the SAME index the user just saw printed.
    internal readonly record struct Numbered(int Index, bool IsDeny, string Rule);

    internal static IReadOnlyList<Numbered> List(Rules rules)
    {
        var list = new List<Numbered>();
        var i = 1;
        foreach (var r in rules.Allow) list.Add(new Numbered(i++, false, r));
        foreach (var r in rules.Deny) list.Add(new Numbered(i++, true, r));
        return list;
    }

    // Remove the rule at 1-based display index `n` (as printed by List). (false, rules) UNCHANGED when out of
    // range — a bad index must never silently mutate the file.
    internal static (bool Ok, Rules Rules) RemoveAt(Rules rules, int n)
    {
        var numbered = List(rules);
        var hit = numbered.FirstOrDefault(x => x.Index == n);
        if (hit.Rule is null) return (false, rules);
        var allow = new List<string>(rules.Allow);
        var deny = new List<string>(rules.Deny);
        if (hit.IsDeny) deny.Remove(hit.Rule); else allow.Remove(hit.Rule);
        return (true, new Rules(allow, deny));
    }

    // ---- persistence (impure) — mirrors CodeSessions' Hash + sessionsRoot test-seam pattern exactly ----

    internal static string RealPermissionsRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".doki", "permissions");

    // PURE (given the override): the one file for this workspace. Unlike sessions (one dir + many timestamped
    // files), permissions are a SINGLE file per workspace, named by the same 12-hex-char hash as CodeSessions.Hash
    // — so a workspace's sessions and its permission rules always agree on which workspace they belong to.
    internal static string FilePath(string root, string? permissionsRoot = null)
        => Path.Combine(permissionsRoot ?? RealPermissionsRoot(), CodeSessions.Hash(root) + ".json");

    // Missing/corrupt/foreign-shaped file all degrade to Rules.Empty — a bad or absent file must never crash
    // startup or silently deny everything; "no rules yet" just means Decide returns Ask for every call, same as
    // today's fully-interactive behavior.
    internal static Rules Load(string root, string? permissionsRoot = null)
    {
        try
        {
            var file = FilePath(root, permissionsRoot);
            if (!File.Exists(file)) return Rules.Empty;
            var parsed = JsonSerializer.Deserialize<Rules>(File.ReadAllText(file));
            return parsed is null ? Rules.Empty : new Rules(parsed.Allow ?? new List<string>(), parsed.Deny ?? new List<string>());
        }
        catch { return Rules.Empty; }
    }

    // Atomic-ish (tmp file + File.Move(overwrite:true)) — same discipline as CodeSessions.Save, so a crash
    // mid-write leaves the PREVIOUS rules file intact. Never throws: false lets the caller note "session only"
    // instead of crashing the REPL over a permissions-file write failure.
    internal static bool Save(string root, Rules rules, string? permissionsRoot = null)
    {
        try
        {
            var dir = permissionsRoot ?? RealPermissionsRoot();
            Directory.CreateDirectory(dir);
            var file = FilePath(root, permissionsRoot);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(rules));
            File.Move(tmp, file, overwrite: true);
            return true;
        }
        catch { return false; }
    }
}
