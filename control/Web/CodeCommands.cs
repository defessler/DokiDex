using System;
using System.Collections.Generic;
using System.IO;

namespace DokiDex.Web;

// Custom slash commands (1.9): user-authored prompt templates as markdown files — a hallmark of Claude Code's
// feel that v1 omitted (F2). `.doki/commands/<name>.md` files each become a `/<name> [args]` command: the file's
// text IS the template, with every `$ARGUMENTS` occurrence replaced by everything the user typed after
// "/<name> " (empty string when there's nothing after it) — no other substitutions in v1. The expanded text then
// runs as a completely NORMAL user turn (Program.cs's HandleCustomCommandAsync) through the same
// RunOneTurnInteractive path as anything hand-typed, so streaming/the context meter/session-save all apply
// unchanged — there is nothing "special" about a custom command once it's expanded.
//
// Discovery is two-root, workspace-local-wins: `<workspace>\.doki\commands\*.md` is checked FIRST, then
// `%USERPROFILE%\.doki\commands\*.md` — a global command is SHADOWED (not merged, not erroring) by a
// workspace-local one of the same name, so a project can override a personal default just by naming a file the
// same thing. Command name = filename without its extension, lowercased (so `Review.md` and `review.md` collide
// on purpose — same command, whichever is found first per root). Built-in slash commands (SlashCommand's own
// switch, Program.cs) ALWAYS win over a custom command of the same name — Discover is only ever consulted after
// the built-in switch's `default:` case, never before it.
//
// `.doki/commands` is deliberately IN-WORKSPACE (unlike `.doki/sessions` and `.doki/permissions`, which live
// centrally under %USERPROFILE% — see CodeSessions/CodePermissions) — commands are project artifacts a team may
// want to commit and share, the same way Claude Code's own `.claude/commands/` is meant to be checked in.
//
// internal (not public): InternalsVisibleTo grants DokiDex.Cli (assembly "doki-code", Program.cs) and
// DokiDex.Control.Tests access — the same seam CodeSessions/CodePermissions already use.
internal static class CodeCommands
{
    // The real global root. Mirrors CodeSessions.RealSessionsRoot / CodePermissions.RealPermissionsRoot: every
    // disk-touching method below takes an optional `globalRoot` override so tests never touch the real
    // %USERPROFILE%\.doki\commands. `workspaceRoot` itself already doubles as the workspace-side test seam — a
    // test just passes a scratch directory for it, exactly like CodeSessions' `root` parameter.
    internal static string RealGlobalRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".doki", "commands");

    // PURE: filename without its extension, lowercased — "Review.md" and "review.md" both name the command
    // "review". Used both by Discover (below) and by Program.cs, which strips the leading '/' off a typed command
    // word before looking it up (the two normalizations agree: both lowercase, no leading '/').
    internal static string CommandNameFromPath(string path)
        => Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

    // PURE (given `arguments`): every "$ARGUMENTS" occurrence in `template` replaced by `arguments` verbatim.
    // `arguments` is already "" when the user typed no trailing text — Program.cs passes that through unchanged,
    // never null. A template with NO "$ARGUMENTS" token at all comes back completely UNCHANGED: v1 does not
    // append the arguments anywhere the template didn't ask for them.
    internal static string ExpandTemplate(string template, string arguments)
        => template.Replace("$ARGUMENTS", arguments);

    // Discover every command visible from `workspaceRoot`: workspace-local (`<workspaceRoot>\.doki\commands\*.md`)
    // is scanned FIRST and always wins; the global root (`globalRoot` override, else RealGlobalRoot()) is scanned
    // second and only FILLS IN names not already claimed by a workspace-local file — a global command is shadowed,
    // never merged or reported as a conflict. Either directory (or both) simply not existing yet is normal (no
    // commands defined) and yields whatever the other root has, never an error. Returns name -> the winning
    // file's full path.
    internal static IReadOnlyDictionary<string, string> Discover(string workspaceRoot, string? globalRoot = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        AddFrom(result, Path.Combine(workspaceRoot, ".doki", "commands"));
        AddFrom(result, globalRoot ?? RealGlobalRoot());
        return result;
    }

    // Adds every "*.md" file's name -> path into `into`, SKIPPING any name already present (first root wins;
    // called workspace-dir-first, then global-dir, so this is exactly the shadowing rule). A missing or unreadable
    // directory degrades to "nothing found here" rather than throwing — command discovery must never crash the
    // REPL loop just because a folder doesn't exist.
    private static void AddFrom(Dictionary<string, string> into, string dir)
    {
        string[] files;
        try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.md") : Array.Empty<string>(); }
        catch { return; }
        foreach (var f in files)
        {
            var name = CommandNameFromPath(f);
            if (!into.ContainsKey(name)) into[name] = f;
        }
    }
}
