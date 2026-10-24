param(
  [Parameter(Mandatory=$true)][ValidateSet('shellmenu','gui')][string]$Scenario,
  [string]$App,
  [int]$HoldSeconds = 6
)
# 用户层自动化：用 UI Automation 读控件、发真实窗口消息，验证「进度真的在动」与「取消真的生效」。
# 说明：拖拽（Explorer → 窗口）这一条由 testtools\ahk\DragDrop.ahk 负责，
# 其余（等待窗口、读文本、读进度条、WinClose 取消）在这里做，因为 UIA 能直接拿到数值。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

$repo = 'C:\Users\Mocha\Desktop\VSProject\Auto7z Project'
if (-not $App) { $App = Join-Path $repo 'Auto7z_WPF\Auto7z_WPF\bin\Debug\Auto7zGUI.exe' }
$dll = Join-Path $repo 'Auto7z_WPF\Auto7z_WPF\bin\Debug\Auto7zShell.dll'
$clsid = '{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}'
$work = Join-Path $env:TEMP 'Auto7zTest_uiauto'
$shotDir = Join-Path $work 'shots'
New-Item -ItemType Directory -Force $shotDir | Out-Null

function Log($msg) {
  $line = "{0:HH:mm:ss.fff}  {1}" -f (Get-Date), $msg
  Write-Host $line
  Add-Content -Path (Join-Path $work 'run.log') -Value $line -Encoding UTF8
}

function Shot([string]$name) {
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
  $path = Join-Path $shotDir "$name.png"
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Log("截图 -> $path")
}

function FindWindowByPid([int]$processId) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
}

function ReadTexts($win) {
  $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $texts = @()
  foreach ($e in $all) {
    if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' -and $e.Current.Name) { $texts += $e.Current.Name }
  }
  return $texts
}

function ReadProgress($win) {
  $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($e in $all) {
    if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ProgressBar') {
      try {
        $pat = $e.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        return [int]$pat.Current.Value
      } catch { return -1 }
    }
  }
  return -1
}

function WaitWindow([int]$processId, [int]$seconds) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    $w = FindWindowByPid $processId
    if ($w) { return $w }
    Start-Sleep -Milliseconds 150
  }
  return $null
}

# ---------------- 准备数据 ----------------
$src = Join-Path $work 'payload'
if (-not (Test-Path $src)) {
  New-Item -ItemType Directory -Force $src | Out-Null
  $rnd = New-Object byte[] (40MB)
  (New-Object System.Random 77).NextBytes($rnd)
  1..6 | ForEach-Object { [IO.File]::WriteAllBytes((Join-Path $src "big$_.bin"), $rnd) }
}
Log("样本目录: $src  $([int](((Get-ChildItem $src | Measure-Object Length -Sum).Sum)/1MB)) MB")

try {
  if ($Scenario -eq 'gui') {
    Log('=== 场景：GUI 主窗口进度（MainText）===')
    $app = Start-Process $App -PassThru
    $win = WaitWindow $app.Id 20
    if (-not $win) { throw '主窗口没有出现' }
    Log("主窗口: [$($win.Current.Name)]")
    Shot 'gui_01_idle'
    Log("初始文本: " + ((ReadTexts $win) -join ' | '))

    # 通过「第二个实例把路径交给已经在跑的实例」喂文件（等价于拖拽/右键的交接路径）
    $handoff = Start-Process $App -ArgumentList "`"$src`"" -PassThru
    $exited = $handoff.WaitForExit(8000)
    Log("交接实例退出=$exited 退出码=$(if($exited){$handoff.ExitCode}else{'仍存活'})")

    $seen = @{}
    $deadline = (Get-Date).AddSeconds($HoldSeconds + 8)
    while ((Get-Date) -lt $deadline) {
      $texts = ReadTexts $win
      $progressLine = $texts | Where-Object { $_ -match '7z|MD5|%' } | Select-Object -First 1
      if ($progressLine) { $seen[$progressLine] = $true }
      if ($seen.Count -in 1,3,8) { Shot ("gui_progress_{0}" -f $seen.Count) }
      if ($texts -match '完成|失败') { break }
      Start-Sleep -Milliseconds 250
    }
    Log("观察到的进度文本 $($seen.Count) 种：")
    $seen.Keys | Sort-Object | ForEach-Object { Log("   $_") }
    Shot 'gui_99_final'
    $app.CloseMainWindow() | Out-Null
    $app.WaitForExit(8000) | Out-Null
  }

  if ($Scenario -eq 'shellmenu') {
    Log('=== 场景：右键菜单进度窗口 + 取消 ===')
    $dp = Join-Path $repo 'testtools\cs\dataprobe\dataprobe.exe'
    $target = $src   # 传整个目录：压缩时间长一点，才看得到进度与取消
    $dpProc = Start-Process $dp -ArgumentList ("`"$dll`" `"$clsid`" `"$target`"") -PassThru
    Log("dataprobe 已启动 pid=$($dpProc.Id) 目标=$target")

    # 等 Auto7zGUI.exe（外壳扩展启动的那个）出现
    $gui = $null
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
      $gui = Get-Process Auto7zGUI -ErrorAction SilentlyContinue | Select-Object -First 1
      if ($gui) { break }
      Start-Sleep -Milliseconds 150
    }
    if (-not $gui) { throw '右键菜单模式没有拉起 Auto7zGUI.exe' }
    $win = WaitWindow $gui.Id 20
    if (-not $win) { throw '进度窗口没有出现' }
    Log("进度窗口: [$($win.Current.Name)] pid=$($gui.Id)")
    Shot 'shell_01_open'

    $samples = @()
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
      $pct = ReadProgress $win
      $txt = (ReadTexts $win | Where-Object { $_ -notmatch '^Auto7z$' } | Select-Object -First 1)
      $samples += "pct=$pct txt=$txt"
      Start-Sleep -Milliseconds 400
    }
    $distinct = ($samples | Select-Object -Unique)
    Log("进度采样 $($samples.Count) 次，不同取值 $($distinct.Count) 种：")
    $distinct | Select-Object -First 8 | ForEach-Object { Log("   $_") }
    Shot 'shell_02_running'

    # 用户层动作：关窗口（等同 Alt+F4 / 标题栏 X）→ 应转成取消
    Log('发送 WinClose（模拟用户点关闭）')
    $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $closed = $gui.WaitForExit(15000)
    Log("进度窗口所属进程退出=$closed")
    Start-Sleep -Seconds 1
    $left7z = @(Get-Process 7z -ErrorAction SilentlyContinue).Count
    Log("残留 7z 进程=$left7z")
    $outDirs = @(Get-ChildItem $src -Directory -Filter '*' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^[A-Za-z0-9_\-]{20,}$' })
    Log("样本目录下的安全输出目录残留=$($outDirs.Count)")
    if (-not $dpProc.HasExited) { $dpProc.Kill() }
    Shot 'shell_99_after'
  }
}
finally {
  foreach ($n in @('Auto7zGUI', 'Auto7z', '7z')) {
    Get-Process $n -ErrorAction SilentlyContinue | Stop-Process -Force
  }
  Log('场景结束')
}
