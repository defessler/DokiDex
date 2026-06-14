# control.ps1 — DokiCode Control: a native desktop window to see status,
# check for updates, and start/stop the stack. A visual shell over doki.ps1.
#
# Launch:  .\control.bat   (or:  pwsh -STA -NoProfile -File control.ps1)

# WPF requires an STA thread; pwsh 7 defaults to MTA, so re-launch self in STA.
if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    Start-Process (Get-Process -Id $PID).Path -ArgumentList @("-NoProfile", "-STA", "-File", "`"$PSCommandPath`"")
    return
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$root     = $PSScriptRoot
$doki     = Join-Path $root "doki.ps1"
$pwsh     = (Get-Process -Id $PID).Path
$script:versions = @{}
$script:updates  = @{}
$script:updateJob = $null

# --- component registry ----------------------------------------------------
$components = @(
    @{ name = "llama-swap";    port = 8080; health = "http://127.0.0.1:8080/v1/models"; installed = { Test-Path (Join-Path $root "serving\llama-swap\llama-swap.exe") } }
    @{ name = "FIM (:8012)";   port = 8012; health = "http://127.0.0.1:8012/health";   installed = { Test-Path (Join-Path $root "models\qwen2.5-coder-3b-q8_0.gguf") } }
    @{ name = "media (:7801)"; port = 7801; health = "http://127.0.0.1:7801/";         installed = { Test-Path (Join-Path $root "media\SwarmUI\src\bin\live_release\SwarmUI.exe") } }
    @{ name = "Crush (CLI)";   port = $null; health = $null; installed = { [bool](Get-Command crush -ErrorAction SilentlyContinue) } }
    @{ name = "Chatbox";       port = $null; health = $null; installed = { Test-Path (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Chatbox.lnk") } }
    @{ name = "models";        port = $null; health = $null; installed = { @(Get-ChildItem (Join-Path $root "models\*.gguf") -ErrorAction SilentlyContinue).Count -gt 0 } }
)

function PortOpen([int]$p) {
    try { $c = New-Object Net.Sockets.TcpClient; $iar = $c.BeginConnect("127.0.0.1", $p, $null, $null); $ok = $iar.AsyncWaitHandle.WaitOne(400); $c.Close(); $ok } catch { $false }
}
function Probe([string]$u) { try { (Invoke-WebRequest $u -TimeoutSec 1 -UseBasicParsing).StatusCode -lt 500 } catch { $false } }

function Get-StatusRows {
    $components | ForEach-Object {
        $c = $_
        $inst = & $c.installed
        $run = if ($c.port) { PortOpen $c.port } else { $null }
        $health = if ($c.health -and $run) { Probe $c.health } else { $null }
        [pscustomobject]@{
            Service   = $c.name
            Installed = if ($inst) { "yes" } else { "NO" }
            Running   = if ($null -eq $c.port) { "-" } elseif ($run) { "UP" } else { "down" }
            Health    = if ($null -eq $c.health) { "-" } elseif ($health) { "ok" } elseif ($run) { "..." } else { "-" }
            Version   = if ($script:versions[$c.name]) { $script:versions[$c.name] } else { "" }
            Update    = if ($script:updates[$c.name]) { $script:updates[$c.name] } else { "" }
        }
    }
}

# --- UI --------------------------------------------------------------------
[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="DokiCode Control" Height="390" Width="620" Background="#1e1e1e" WindowStartupLocation="CenterScreen">
  <Grid Margin="12">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>
    <TextBlock Grid.Row="0" Text="DokiCode — local AI stack" Foreground="#dddddd" FontSize="16" FontWeight="Bold" Margin="0,0,0,8"/>
    <DataGrid Grid.Row="1" Name="Grid" AutoGenerateColumns="False" IsReadOnly="True" HeadersVisibility="Column"
              Background="#252526" Foreground="#dddddd" RowBackground="#252526" AlternatingRowBackground="#2d2d30"
              GridLinesVisibility="Horizontal" BorderThickness="0" FontFamily="Consolas">
      <DataGrid.Columns>
        <DataGridTextColumn Header="Service"   Binding="{Binding Service}"   Width="130"/>
        <DataGridTextColumn Header="Installed" Binding="{Binding Installed}" Width="80"/>
        <DataGridTextColumn Header="Running"   Binding="{Binding Running}"   Width="80"/>
        <DataGridTextColumn Header="Health"    Binding="{Binding Health}"    Width="70"/>
        <DataGridTextColumn Header="Version"   Binding="{Binding Version}"   Width="95"/>
        <DataGridTextColumn Header="Update"    Binding="{Binding Update}"    Width="*"/>
      </DataGrid.Columns>
    </DataGrid>
    <WrapPanel Grid.Row="2" Margin="0,10,0,0">
      <Button Name="BtnAgent"   Content="Agent"        Margin="0,0,6,0" Padding="10,4"/>
      <Button Name="BtnCoexist" Content="Coexist"      Margin="0,0,6,0" Padding="10,4"/>
      <Button Name="BtnMedia"   Content="Media"        Margin="0,0,6,0" Padding="10,4"/>
      <Button Name="BtnStop"    Content="Stop"         Margin="0,0,12,0" Padding="10,4"/>
      <Button Name="BtnVerify"  Content="Verify"       Margin="0,0,6,0" Padding="10,4"/>
      <Button Name="BtnUpdates" Content="Check Updates" Margin="0,0,6,0" Padding="10,4"/>
      <Button Name="BtnUI"      Content="Open :8080/ui" Margin="0,0,6,0" Padding="10,4"/>
    </WrapPanel>
    <TextBlock Grid.Row="3" Name="StatusBar" Text="ready" Foreground="#9cdcfe" Margin="0,10,0,0" TextWrapping="Wrap"/>
  </Grid>
</Window>
"@

$win = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$Grid = $win.FindName("Grid"); $StatusBar = $win.FindName("StatusBar")

function RunDoki([string[]]$a, [switch]$Visible) {
    $args = @("-NoProfile"); if ($Visible) { $args += "-NoExit" }; $args += @("-File", $doki) + $a
    Start-Process $pwsh -ArgumentList $args -WindowStyle $(if ($Visible) { "Normal" } else { "Hidden" })
}
function Refresh { $Grid.ItemsSource = @(Get-StatusRows) }

$win.FindName("BtnAgent").Add_Click({   $StatusBar.Text = "starting agent (chat + code)...";        RunDoki @("up", "agent") })
$win.FindName("BtnCoexist").Add_Click({ $StatusBar.Text = "starting coexist (+ autocomplete)...";    RunDoki @("up", "coexist") })
$win.FindName("BtnMedia").Add_Click({   $StatusBar.Text = "starting media (stops the LLM)...";       RunDoki @("up", "media") })
$win.FindName("BtnStop").Add_Click({    $StatusBar.Text = "stopping all services...";               RunDoki @("down") })
$win.FindName("BtnVerify").Add_Click({  $StatusBar.Text = "running full-stack verify (see console)..."; RunDoki @("verify") -Visible })
$win.FindName("BtnUI").Add_Click({      Start-Process "http://127.0.0.1:8080/ui" })
$win.FindName("BtnUpdates").Add_Click({
    if ($script:updateJob) { return }
    $StatusBar.Text = "checking for updates (winget / git / github)..."
    $script:updateJob = Start-Job -ScriptBlock {
        param($root)
        $v = @{}; $u = @{}
        try { $v["Crush (CLI)"] = (& crush --version 2>$null | Select-Object -First 1) } catch {}
        $wu = (winget upgrade 2>$null) -join "`n"
        $u["Crush (CLI)"] = if ($wu -match 'charmbracelet\.crush') { "UPDATE" } else { "current" }
        $u["Chatbox"]     = if ($wu -match 'Bin-Huang\.Chatbox') { "UPDATE" } else { "current" }
        $sw = Join-Path $root "media\SwarmUI"
        if (Test-Path (Join-Path $sw ".git")) {
            git -C $sw fetch -q 2>$null
            $v["media (:7801)"] = (git -C $sw describe --tags --always 2>$null)
            $behind = (git -C $sw rev-list --count "HEAD..origin/HEAD" 2>$null)
            $u["media (:7801)"] = if ([int]$behind -gt 0) { "$behind behind" } else { "current" }
        }
        try { $u["llama-swap"] = "latest " + (gh api repos/mostlygeek/llama-swap/releases/latest --jq .tag_name 2>$null) } catch {}
        @{ versions = $v; updates = $u }
    } -ArgumentList $root
})

$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(4)
$timer.Add_Tick({
    Refresh
    if ($script:updateJob -and $script:updateJob.State -in 'Completed', 'Failed') {
        $res = Receive-Job $script:updateJob -ErrorAction SilentlyContinue
        Remove-Job $script:updateJob -Force -ErrorAction SilentlyContinue; $script:updateJob = $null
        if ($res) { $script:versions = $res.versions; $script:updates = $res.updates; $StatusBar.Text = "updates checked $(Get-Date -Format 'HH:mm:ss')" }
        else { $StatusBar.Text = "update check finished (no data)" }
        Refresh
    }
})
$timer.Start()
Refresh
$win.Add_Closed({ $timer.Stop(); if ($script:updateJob) { Remove-Job $script:updateJob -Force -ErrorAction SilentlyContinue } })
[void]$win.ShowDialog()
