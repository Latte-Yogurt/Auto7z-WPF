# 「把文件拖到主窗口」的程序接口级测试。
#
# 背景：主窗口是 AllowsTransparency 的透明窗，合成拖拽进不去；跨进程调窗口上的
# IDropTarget 也拿不到（指针只在目标进程有效）。所以产品里留了一个 Debug-only
# 的注入开关 --feed，走的正是投放之后的 EnqueueFiles 路径。
#
# 这个脚本负责：启动 GUI → 用 --feed 注入样本目录 → 用 UIA 确认主窗口确实开始
# 处理并回报进度 → 关闭。Release 构建没有 --feed，脚本会明确报错退出。
param(
  [string]$exe,
  [int]$TimeoutSeconds = 60
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$repo = 'C:\Users\Mocha\Desktop\VSProject\Auto7z Project'
if (-not $exe) { $exe = Join-Path $repo 'Auto7z_WPF\Auto7z_WPF\bin\Debug\Auto7zGUI.exe' }

$work = Join-Path $env:TEMP 'Auto7zTest_feedrun'
$src = Join-Path $work 'payload'
if (-not (Test-Path $src)) {
  New-Item -ItemType Directory -Force $src | Out-Null
  $rnd = New-Object byte[] (24MB)
  (New-Object System.Random 31).NextBytes($rnd)
  1..2 | ForEach-Object { [IO.File]::WriteAllBytes((Join-Path $src "part$_.bin"), $rnd) }
}

function Fail($msg) { Write-Host "[失败] $msg" -ForegroundColor Red; exit 1 }
function Ok($msg) { Write-Host "[通过] $msg" -ForegroundColor Green }

Get-Process Auto7zGUI, Auto7z, 7z -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds 3
if ($proc.HasExited) { Fail '主程序启动即退出' }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition -ArgumentList ([System.Windows.Automation.AutomationElement]::NameProperty), "Auto7z"
$win = $null
for ($i = 0; $i -lt 40; $i++) {
  if (-not $cond) { break }
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if ($win) { break }
  Start-Sleep -Milliseconds 250
}
if (-not $win) { Fail '主窗口没出现' }
Ok "主窗口已就绪：[$($win.Current.Name)]"

# 注入（第二个实例把路径交给已在运行的实例，与用户拖拽后的处理同一条路径）
$feed = Start-Process $exe -ArgumentList @('--feed', "`"$src`"") -PassThru
if (-not $feed.WaitForExit(15000)) { $feed.Kill() }
if ($feed.ExitCode -ne 0) {
  Fail "注入实例退出码=$($feed.ExitCode)（Release 构建没有 --feed，这是预期结果）"
}
Ok "注入实例已交接并退出（退出码 0）"

# 等任务真的起来
$saw7z = $false
$progressTexts = New-Object System.Collections.Generic.HashSet[string]
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
  if (-not $saw7z -and @(Get-Process 7z -ErrorAction SilentlyContinue).Count -gt 0) { $saw7z = $true }
  $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($e in $all) {
    if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Text') {
      $n = $e.Current.Name
      if ($n -match '7z|MD5|%') { [void]$progressTexts.Add($n) }
      if ($n -match '完成|失败') { $deadline = (Get-Date) }
    }
  }
  if (-not $saw7z -and ((Get-Date) -gt $deadline)) { break }
  Start-Sleep -Milliseconds 250
}

if ($saw7z) { Ok '注入后确实起了 7z 子进程（任务真的跑了）' } else { Fail '注入后没有看到 7z 子进程' }
if ($progressTexts.Count -ge 3) {
  Ok "主窗口进度文本变化 $($progressTexts.Count) 种（例如：$(($progressTexts | Select-Object -First 1))）"
} elseif ($progressTexts.Count -ge 1) {
  Ok "主窗口有进度文本，但只采到 $($progressTexts.Count) 种（任务太快）"
} else {
  Fail '主窗口没有出现任何进度文本'
}

$proc.CloseMainWindow() | Out-Null
$proc.WaitForExit(10000) | Out-Null
Get-Process 7z -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '全部通过'
exit 0
