using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Studio UX polish (feat/studio-ux): the two PURE client reducers that gate the consolidated Edit surface and the
// resizable live pane are extracted into a marked DD-PURE block in the embedded index.html. There is NO Node test
// runner wired into this repo, so — per the brief — we keep the check inside the already-wired `dotnet test` suite:
// this test reads the SAME embedded SPA the host serves (DokiDex.studio.index.html), slices out the DD-PURE block,
// and evaluates the REAL JavaScript via `node` to assert the truth tables. If `node` isn't on PATH the test SKIPS
// (Assert.True with a message) rather than failing CI on a missing optional tool — it never silently passes on a
// broken reducer when Node IS present. This locks two contracts a future edit must not break:
//   (1) editSurface(kind,hasInit,maskDrawn,editMode) routes the right region AND only sets sendMask on the Inpaint
//       tab (so a mask painted on Inpaint then left while switching to Sketch/Outpaint is NOT POSTed to /api/generate),
//       with a never-blank fallback to 'sketch' when the active tab can't show for the current kind/init.
//   (2) clampWidth(req,containerW) bounds the live-pane width to [240, min(960, containerW)].
public class EditSurfaceTests
{
    private static string LoadIndexHtml()
    {
        // The exact resource the StudioHost serves (and the single-file exe embeds) — same bytes, no wwwroot on disk.
        var asm = typeof(StudioHost).Assembly;
        using var s = asm.GetManifestResourceStream("DokiDex.studio.index.html");
        Assert.True(s is not null, "embedded SPA resource DokiDex.studio.index.html missing from the build");
        using var r = new StreamReader(s!);
        return r.ReadToEnd();
    }

    private static string ExtractPureBlock(string html)
    {
        var m = Regex.Match(html, @"// ===== DD-PURE-BEGIN[\s\S]*?// ===== DD-PURE-END =====");
        Assert.True(m.Success, "DD-PURE marked block not found in the embedded index.html (test seam removed?)");
        return m.Value;
    }

    private static string? NodePath()
    {
        // Resolve `node` on PATH (Windows: node.exe / node.cmd). Returns null when Node isn't installed.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var name in new[] { "node.exe", "node.cmd", "node" })
            {
                try { var full = Path.Combine(dir.Trim(), name); if (File.Exists(full)) return full; }
                catch { /* malformed PATH entry */ }
            }
        }
        return null;
    }

    // The headless harness: eval the extracted block, then assert both truth tables. Exits non-zero with a FAIL line
    // on any mismatch; prints OK on success. (Kept as JS so it exercises the REAL reducers, not a C# re-implementation.)
    private const string Harness = @"
const fs=require('fs');
const block=fs.readFileSync(process.argv[2],'utf8');  // argv: [node, harness.js, block.js] -> block is [2]
const {editSurface, clampWidth}=new Function(block+'\n; return {editSurface, clampWidth};')();
let fails=0; const ok=(c,m)=>{ if(!c){ fails++; console.log('FAIL: '+m); } };
let s;
// editSurface truth table -------------------------------------------------------------------------
s=editSurface('image',false,false,'sketch'); ok(s.showSketch&&!s.showInpaint&&!s.showOutpaint&&!s.sendMask,'image/sketch -> sketch only');
s=editSurface('image',true,true,'inpaint');  ok(s.showSketch&&!s.showInpaint&&!s.sendMask&&s.effMode==='sketch','image/inpaint -> fallback sketch');
s=editSurface('image',true,true,'outpaint'); ok(s.showSketch&&!s.showOutpaint&&s.effMode==='sketch','image/outpaint -> fallback sketch');
s=editSurface('edit',true,true,'sketch');    ok(s.showSketch&&!s.showInpaint&&!s.sendMask,'edit+init/sketch -> sketch, mask NOT sent');
s=editSurface('edit',true,true,'inpaint');   ok(s.showInpaint&&!s.showSketch&&s.sendMask===true,'edit+init/inpaint+drawn -> sendMask true');
s=editSurface('edit',true,false,'inpaint');  ok(s.showInpaint&&s.sendMask===false,'edit+init/inpaint not-drawn -> sendMask false');
s=editSurface('edit',true,true,'outpaint');  ok(s.showOutpaint&&!s.showInpaint&&s.sendMask===false,'edit+init/outpaint -> outpaint, no mask');
s=editSurface('edit',false,true,'inpaint');  ok(s.showSketch&&!s.showInpaint&&s.sendMask===false&&s.effMode==='sketch','edit/no-init/inpaint -> fallback sketch');
s=editSurface('edit',true,true,'sketch');    ok(s.sendMask===false,'STALE-MASK GUARD: editMode!==inpaint -> sendMask false');
s=editSurface('music',true,true,'sketch');   ok(!s.showSketch&&!s.showInpaint&&!s.showOutpaint&&!s.sendMask,'music -> no edit regions');
// clampWidth --------------------------------------------------------------------------------------
ok(clampWidth(100,2000)===240,'clamp below min -> 240');
ok(clampWidth(5000,2000)===960,'clamp above max -> 960');
ok(clampWidth(800,500)===500,'clamp above containerW -> containerW');
ok(clampWidth(400,2000)===400,'in-range -> req');
ok(clampWidth(960,960)===960,'boundary 960/960 -> 960');
if(fails){ console.log('TRUTH-TABLE: '+fails+' FAILURES'); process.exit(1); }
console.log('OK');
";

    [Fact]
    public void Embedded_index_contains_the_pure_edit_surface_seam()
    {
        // Cheap, Node-free guard: the markers + both pure fns are present in the bytes the build actually embeds.
        var html = LoadIndexHtml();
        var block = ExtractPureBlock(html);
        Assert.Contains("function editSurface(", block);
        Assert.Contains("function clampWidth(", block);
        // The mask gate in generate() MUST consult the reducer (so it requires editMode==='inpaint'), not the old
        // raw `state.kind==='edit' && state.initImage && _maskDrawn` condition.
        Assert.Contains("editSurface(state.kind, !!state.initImage, _maskDrawn, state.editMode).sendMask) body.maskImage", html);
        // Exactly ONE updateMaskBox declaration (the prior multi-agent bug was a duplicate that hoisting let win).
        var maskBoxDecls = Regex.Matches(html, @"function updateMaskBox\(").Count;
        Assert.True(maskBoxDecls == 1, $"expected exactly one updateMaskBox declaration, found {maskBoxDecls}");
    }

    [Fact]
    public void EditSurface_and_clampWidth_truth_tables_hold_in_real_js()
    {
        var html = LoadIndexHtml();
        var block = ExtractPureBlock(html);

        var node = NodePath();
        if (node is null)
        {
            // Optional tool absent: skip rather than fail CI. (When Node IS present this asserts the real reducers.)
            Assert.True(true, "node not on PATH — skipping the headless JS truth-table run");
            return;
        }

        var blockFile = Path.Combine(Path.GetTempPath(), "dd_pure_" + Guid.NewGuid().ToString("N") + ".js");
        var harnessFile = Path.Combine(Path.GetTempPath(), "dd_harness_" + Guid.NewGuid().ToString("N") + ".js");
        try
        {
            File.WriteAllText(blockFile, block, new UTF8Encoding(false));
            File.WriteAllText(harnessFile, Harness, new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = node,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(harnessFile);
            psi.ArgumentList.Add(blockFile);

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);

            Assert.True(p.HasExited && p.ExitCode == 0,
                $"headless JS truth-table run failed (exit {(p.HasExited ? p.ExitCode.ToString() : "timeout")}):\n{stdout}\n{stderr}");
            Assert.Contains("OK", stdout);
        }
        finally
        {
            try { File.Delete(blockFile); } catch { }
            try { File.Delete(harnessFile); } catch { }
        }
    }
}
