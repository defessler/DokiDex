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
        Title="DokiCode Control" Height="450" Width="640" Background="#1e1e1e"
        WindowStartupLocation="CenterScreen">
  <Window.Resources>
    <!-- Buttons: flat, dark, color-coded, with hover/press feedback -->
    <Style x:Key="Btn" TargetType="Button">
      <Setter Property="Foreground" Value="#ffffff"/>
      <Setter Property="Background" Value="#3a3d41"/>
      <Setter Property="Padding" Value="14,6"/>
      <Setter Property="Margin" Value="0,0,6,6"/>
      <Setter Property="FontSize" Value="12"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="SnapsToDevicePixels" Value="True"/>
      <Setter Property="ToolTipService.InitialShowDelay" Value="300"/>
      <Setter Property="ToolTipService.ShowDuration" Value="20000"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="4"
                    BorderThickness="1" BorderBrush="Transparent" Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="bd" Property="BorderBrush" Value="#b0ffffff"/>
              </Trigger>
              <Trigger Property="IsPressed" Value="True">
                <Setter TargetName="bd" Property="Opacity" Value="0.75"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key="BtnPrimary" TargetType="Button" BasedOn="{StaticResource Btn}">
      <Setter Property="Background" Value="#0e639c"/>
    </Style>
    <Style x:Key="BtnDanger" TargetType="Button" BasedOn="{StaticResource Btn}">
      <Setter Property="Background" Value="#a1260d"/>
    </Style>
    <!-- Dark column headers (the default ones are light and clash) -->
    <Style TargetType="DataGridColumnHeader">
      <Setter Property="Background" Value="#333337"/>
      <Setter Property="Foreground" Value="#ffffff"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Padding" Value="8,6"/>
      <Setter Property="BorderThickness" Value="0,0,1,1"/>
      <Setter Property="BorderBrush" Value="#3f3f46"/>
      <Setter Property="HorizontalContentAlignment" Value="Left"/>
    </Style>
    <!-- Status cells: bold + semantic color so good/bad reads at a glance -->
    <Style x:Key="CellBase" TargetType="DataGridCell">
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Padding" Value="8,3"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>
    <Style x:Key="CellInstalled" TargetType="DataGridCell" BasedOn="{StaticResource CellBase}">
      <Style.Triggers>
        <DataTrigger Binding="{Binding Installed}" Value="yes"><Setter Property="Foreground" Value="#6a9955"/></DataTrigger>
        <DataTrigger Binding="{Binding Installed}" Value="NO"><Setter Property="Foreground" Value="#f14c4c"/></DataTrigger>
      </Style.Triggers>
    </Style>
    <Style x:Key="CellRunning" TargetType="DataGridCell" BasedOn="{StaticResource CellBase}">
      <Style.Triggers>
        <DataTrigger Binding="{Binding Running}" Value="UP"><Setter Property="Foreground" Value="#4ec9b0"/></DataTrigger>
        <DataTrigger Binding="{Binding Running}" Value="down"><Setter Property="Foreground" Value="#808080"/></DataTrigger>
      </Style.Triggers>
    </Style>
    <Style x:Key="CellHealth" TargetType="DataGridCell" BasedOn="{StaticResource CellBase}">
      <Style.Triggers>
        <DataTrigger Binding="{Binding Health}" Value="ok"><Setter Property="Foreground" Value="#4ec9b0"/></DataTrigger>
        <DataTrigger Binding="{Binding Health}" Value="..."><Setter Property="Foreground" Value="#9cdcfe"/></DataTrigger>
      </Style.Triggers>
    </Style>
    <Style x:Key="CellUpdate" TargetType="DataGridCell" BasedOn="{StaticResource CellBase}">
      <Style.Triggers>
        <DataTrigger Binding="{Binding Update}" Value="UPDATE"><Setter Property="Foreground" Value="#d7ba7d"/></DataTrigger>
        <DataTrigger Binding="{Binding Update}" Value="current"><Setter Property="Foreground" Value="#808080"/></DataTrigger>
      </Style.Triggers>
    </Style>
  </Window.Resources>
  <Grid Margin="14">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>
    <TextBlock Grid.Row="0" Text="DokiCode — local AI stack" Foreground="#ffffff" FontSize="16" FontWeight="Bold" Margin="0,0,0,10"/>
    <DataGrid Grid.Row="1" Name="Grid" AutoGenerateColumns="False" IsReadOnly="True" HeadersVisibility="Column"
              CanUserSortColumns="False" CanUserResizeColumns="False" SelectionMode="Single" RowHeaderWidth="0"
              Background="#252526" Foreground="#d4d4d4" RowBackground="#252526" AlternatingRowBackground="#2d2d30"
              GridLinesVisibility="Horizontal" HorizontalGridLinesBrush="#3f3f46"
              BorderThickness="1" BorderBrush="#3f3f46" FontFamily="Consolas" RowHeight="26">
      <DataGrid.Columns>
        <DataGridTextColumn Header="Service"   Binding="{Binding Service}"   Width="130"/>
        <DataGridTextColumn Header="Installed" Binding="{Binding Installed}" Width="80"  CellStyle="{StaticResource CellInstalled}"/>
        <DataGridTextColumn Header="Running"   Binding="{Binding Running}"   Width="80"  CellStyle="{StaticResource CellRunning}"/>
        <DataGridTextColumn Header="Health"    Binding="{Binding Health}"    Width="70"  CellStyle="{StaticResource CellHealth}"/>
        <DataGridTextColumn Header="Version"   Binding="{Binding Version}"   Width="95"/>
        <DataGridTextColumn Header="Update"    Binding="{Binding Update}"    Width="*"   CellStyle="{StaticResource CellUpdate}"/>
      </DataGrid.Columns>
    </DataGrid>
    <StackPanel Grid.Row="2" Margin="0,12,0,0">
      <TextBlock Text="MODE  ·  the GPU runs one at a time" Foreground="#858585" FontSize="11" Margin="2,0,0,5"/>
      <WrapPanel>
        <Button Name="BtnAgent"   Content="Agent"   Style="{StaticResource BtnPrimary}" ToolTip="Chat + code. Starts the LLM (llama-swap) on :8080. Your everyday default."/>
        <Button Name="BtnCoexist" Content="Coexist" Style="{StaticResource BtnPrimary}" ToolTip="Chat + code + editor autocomplete. Adds the FIM model on :8012 next to the LLM."/>
        <Button Name="BtnMedia"   Content="Media"   Style="{StaticResource BtnPrimary}" ToolTip="Image + video. Stops the LLM and starts SwarmUI on :7801 — the GPU can't run both at once."/>
        <Button Name="BtnStop"    Content="Stop"    Style="{StaticResource BtnDanger}"  ToolTip="Stop every service and free the GPU."/>
      </WrapPanel>
      <TextBlock Text="ACTIONS" Foreground="#858585" FontSize="11" Margin="2,12,0,5"/>
      <WrapPanel>
        <Button Name="BtnVerify"  Content="Verify"        Style="{StaticResource Btn}" ToolTip="Full-stack smoke test: cycles agent → coexist → media and checks each capability for real. Opens a console window."/>
        <Button Name="BtnUpdates" Content="Check Updates" Style="{StaticResource Btn}" ToolTip="Look for newer versions — winget (Crush, Chatbox), git (SwarmUI), GitHub (llama-swap). Results land in the Update column."/>
        <Button Name="BtnUI"      Content="Open :8080/ui" Style="{StaticResource Btn}" ToolTip="Open the llama-swap web UI in your browser. Needs Agent or Coexist running."/>
      </WrapPanel>
    </StackPanel>
    <TextBlock Grid.Row="3" Name="StatusBar" Text="ready" Foreground="#9cdcfe" Margin="0,12,0,0" TextWrapping="Wrap"/>
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
