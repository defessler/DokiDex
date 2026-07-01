using System.Text;
using DokiDex.Web;

namespace DokiDex.Cli;

// doki code — a local terminal coding agent that mirrors the Claude Code CLI, running the user's local coder model
// (coder-fast/coder-big via llama-swap) through CodeAgent's ReAct loop. The workspace is the current directory, so
// you `cd` into any project and run it. Read/Grep run freely; Edit/Write/Bash show a diff or the command and wait
// for your approval (y once / a always / n no). Slash commands: /help /model /clear /cwd /exit. Ctrl+C interrupts.
internal static class Program
{
    private static CancellationTokenSource? _turnCts;

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* some terminals reject this — ignore */ }
        var root = Directory.GetCurrentDirectory();
        var model = "coder-fast";
        string? oneShot = null;
        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--model" or "-m") && i + 1 < args.Length) model = args[++i];
            else if ((args[i] is "--print" or "-p") && i + 1 < args.Length) oneShot = args[++i];
            else if (args[i] == "--cwd" && i + 1 < args.Length) root = args[++i];
            else if (!args[i].StartsWith('-')) oneShot ??= args[i];
        }
        if (!Directory.Exists(root)) root = Directory.GetCurrentDirectory();

        var working = new List<object> { new { role = "system", content = CodeAgent.SystemPrompt } };
        var always = new HashSet<string>();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _turnCts?.Cancel(); };

        // One-shot mode (doki code -p "task") — run a single turn and exit, for scripting.
        if (oneShot is not null)
        {
            await RunOneTurn(root, working, model, always, oneShot);
            return 0;
        }

        Banner(root, model);
        while (true)
        {
            Paint(ConsoleColor.Cyan, "\n› ");
            var line = Console.ReadLine();
            if (line is null) { Console.WriteLine(); break; }   // EOF / Ctrl+Z
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '/')
            {
                if (!SlashCommand(line, ref model, working, root)) break;
                continue;
            }
            await RunOneTurn(root, working, model, always, line);
        }
        return 0;
    }

    private static async Task RunOneTurn(string root, List<object> working, string model, HashSet<string> always, string userText)
    {
        working.Add(new { role = "user", content = userText });
        _turnCts = new CancellationTokenSource();
        try
        {
            var text = await CodeAgent.RunTurnAsync(root, working, model, Approve, always, ShowTool, ShowToolResult, ShowAssistantText, _turnCts.Token);
            Console.WriteLine();
            Paint(ConsoleColor.Gray, text + "\n");
        }
        catch (OperationCanceledException) { Paint(ConsoleColor.DarkGray, "\n(interrupted)\n"); }
        catch (Exception ex) { Paint(ConsoleColor.Red, $"\nerror: {ex.Message}\n"); }
        finally { _turnCts?.Dispose(); _turnCts = null; }
    }

    // The approval gate: show a colored diff (Edit/Write) or the command (Bash), then read a single key.
    // Default (Enter / any other key) is NO — the safe choice for a security-conscious local agent.
    private static CodeAgent.ApprovalDecision Approve(CodeAgent.PendingAction a)
    {
        Console.WriteLine();
        if (string.Equals(a.Tool, "Bash", StringComparison.OrdinalIgnoreCase))
            Paint(ConsoleColor.Yellow, $"  $ {a.Preview}\n");
        else
            PrintDiff(a.Preview);
        Paint(ConsoleColor.Cyan, $"  Allow {a.Tool}? [y]es / [a]lways / [n]o: ");
        ConsoleKeyInfo key;
        try { key = Console.ReadKey(); } catch { Console.WriteLine(); return CodeAgent.ApprovalDecision.Deny; }
        Console.WriteLine();
        return char.ToLowerInvariant(key.KeyChar) switch
        {
            'a' => CodeAgent.ApprovalDecision.Always,
            'y' => CodeAgent.ApprovalDecision.Once,
            _ => CodeAgent.ApprovalDecision.Deny,
        };
    }

    private static void ShowTool(string line) => Paint(ConsoleColor.Cyan, "\n" + line + "\n");

    private static void ShowAssistantText(string text) => Paint(ConsoleColor.Gray, "\n" + text + "\n");

    private static void ShowToolResult(string name, string result)
    {
        var first = result.Replace("\r", "").Split('\n')[0];
        if (first.Length > 100) first = first[..100] + "…";
        Paint(ConsoleColor.DarkGray, "  ⎿ " + first + "\n");
    }

    private static void PrintDiff(string diff)
    {
        foreach (var l in diff.Replace("\r", "").Split('\n'))
        {
            var c = l.StartsWith("+ ") ? ConsoleColor.Green
                  : l.StartsWith("- ") ? ConsoleColor.Red
                  : ConsoleColor.DarkGray;
            Paint(c, "  " + l + "\n");
        }
    }

    private static bool SlashCommand(string line, ref string model, List<object> working, string root)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit":
            case "/quit":
                return false;
            case "/help":
                Paint(ConsoleColor.Gray,
                    "  Commands: /help  /model <name>  /undo  /clear  /cwd  /exit\n" +
                    "  The agent uses Read, Grep, Edit, Write, Bash — you approve each change & command.\n" +
                    "  /undo reverts the last file change from this session (edits are plain working-tree changes).\n");
                return true;
            case "/model":
                if (parts.Length > 1) { model = parts[1].Trim(); Paint(ConsoleColor.Gray, $"  model → {model}\n"); }
                else Paint(ConsoleColor.Gray, $"  model = {model}  (coder-fast | coder-big | fast-candidate-gptoss20b)\n");
                return true;
            case "/clear":
                if (working.Count > 1) working.RemoveRange(1, working.Count - 1);   // keep the system message
                Paint(ConsoleColor.Gray, "  context cleared.\n");
                return true;
            case "/cwd":
                Paint(ConsoleColor.Gray, $"  workspace: {root}\n");
                return true;
            case "/undo":
                Paint(ConsoleColor.Gray, "  " + CodeAgent.Undo() + "\n");
                return true;
            default:
                Paint(ConsoleColor.DarkGray, $"  unknown command {parts[0]} — try /help\n");
                return true;
        }
    }

    private static void Banner(string root, string model)
    {
        Paint(ConsoleColor.Cyan, "\n  doki code");
        Paint(ConsoleColor.DarkGray, $"  · local coding agent · {model}\n");
        Paint(ConsoleColor.DarkGray, $"  workspace: {root}\n");
        Paint(ConsoleColor.DarkGray, "  type a task, or /help · Ctrl+C interrupts · /exit to quit\n");
    }

    private static void Paint(ConsoleColor c, string s)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = c;
        Console.Write(s);
        Console.ForegroundColor = prev;
    }
}
