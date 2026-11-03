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

# 用外壳扩展那套共享内存协议，把路径交给 Auto7zGUI.exe，让它进「右键菜单模式」。
#
# 为什么不直接跑 dataprobe + Auto7zShell.dll：
#   外壳扩展会去注册表读「主程序路径」，机器上装过一份 Auto7z 时它指向安装目录里的
#   旧版本，于是被测的就不是当前构建了。这里用同一套 #映射名:字节数:事件名 协议
#   直接启动当前构建的 Auto7zGUI.exe —— 走的是完全相同的分支
#   （ShellCommandService 认出 # 前缀 -> fromShell=true -> RunFromShellMenu），
#   只是不经过外壳扩展。外壳扩展本身另有 shellcheck/scopeprobe/iconprobe 覆盖。
#
# 返回一个包含 Exe / Arg / Signal / Map 的对象；调用方用完要 Dispose 句柄。
function StartShellMenu([string[]]$paths, [string]$exePath) {
  # MemoryMappedFile / EventWaitHandle 都在 mscorlib 里，不用额外 Add-Type。

  $totalChars = 1   # 开头一个标记字符
  foreach ($p in $paths) { $totalChars += $p.Length + 1 }
  $totalBytes = $totalChars * 2

  $suffix = ([guid]::NewGuid().ToString("N")).Substring(0, 8)
  $mapName = "Auto7zUiProbeMap$suffix"
  $eventName = "Auto7zUiProbeEvent$suffix"

  $mapping = [System.IO.MemoryMappedFiles.MemoryMappedFile]::CreateNew(
    $mapName, $totalBytes, [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::ReadWrite)

  $accessor = $mapping.CreateViewAccessor(0, $totalBytes,
    [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::ReadWrite)
  $buffer = New-Object byte[] $totalBytes
  $position = 2   # 跳过开头的标记（2 字节 = 1 个 wchar）
  foreach ($p in $paths) {
    $encoded = [System.Text.Encoding]::Unicode.GetBytes($p)
    [Array]::Copy($encoded, 0, $buffer, $position, $encoded.Length)
    $position += $encoded.Length
    $buffer[$position] = 0; $buffer[$position + 1] = 0
    $position += 2
  }
  $accessor.WriteArray(0, $buffer, 0, $totalBytes)
  $accessor.Flush()
  $accessor.Dispose()

  # 映射句柄不能关：命名映射对象靠句柄存活，子进程还没读完就关了会读不到。
  $signal = New-Object System.Threading.EventWaitHandle(
    $false, [System.Threading.EventResetMode]::ManualReset, $eventName)

  return [pscustomobject]@{
    Exe = $exePath
    Arg = "#$mapName`:$totalBytes`:$eventName"
    Signal = $signal
    Map = $mapping
  }
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
    # 进程句柄不能叫 $app：PowerShell 变量名不区分大小写，$app 与参数 $App
    # 是同一个变量，而 $App 带 [string] 类型约束，赋 Process 进去会被强制转成字符串，
    # 后面 $app.Id 就恒为 null，窗口永远等不到。
    $guiProc = Start-Process $App -PassThru
    $win = WaitWindow $guiProc.Id 20
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
      # 窗口标题就是 Auto7z，里面也含 7z，直接按 7z 过滤会把标题当成进度。
      # 改成只认带百分号或 MD5 的那条。
      $progressLine = $texts | Where-Object { $_ -match '%|MD5' } | Select-Object -First 1
      if ($progressLine) { $seen[$progressLine] = $true }
      if ($seen.Count -in 1,3,8) { Shot ("gui_progress_{0}" -f $seen.Count) }
      if ($texts -match '完成|失败') { break }
      Start-Sleep -Milliseconds 250
    }
    Log("观察到的进度文本 $($seen.Count) 种：")
    $seen.Keys | Sort-Object | ForEach-Object { Log("   $_") }
    Shot 'gui_99_final'
    $guiProc.CloseMainWindow() | Out-Null
    $guiProc.WaitForExit(8000) | Out-Null
  }

  if ($Scenario -eq 'shellmenu') {
    Log('=== 场景：右键菜单进度窗口 + 取消 ===')
    $target = $src   # 传整个目录：压缩时间长一点，才看得到进度与取消
    $menu = StartShellMenu @($target) $App
    Log("启动外壳菜单模式: $($menu.Exe) 参数 $($menu.Arg)")
    $gui = Start-Process $menu.Exe -ArgumentList "`"$($menu.Arg)`" -Z" -PassThru
    Log("Auto7zGUI 已启动 pid=$($gui.Id) 目标=$target")

    $win = WaitWindow $gui.Id 20
    if (-not $win) { throw '进度窗口没有出现' }
    Log("进度窗口: [$($win.Current.Name)] pid=$($gui.Id)")
    Shot 'shell_01_open'

    $samples = @()
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
      $pct = ReadProgress $win
      # 进度窗口正文是 PromptText，标题栏文字也是同一个词，
      # 所以把窗口里所有文本都记下来，而不是只取第一个 ——
      # 只看第一个会读到标题，看不出正文有没有带上百分比。
      $all = ReadTexts $win
      $txt = ($all | Where-Object { $_ -notmatch '^Auto7z$' } | Select-Object -First 1)
      $samples += "pct=$pct txt=$txt all=$($all -join ',')"
      Start-Sleep -Milliseconds 400
    }
    $distinct = ($samples | Select-Object -Unique)
    Log("进度采样 $($samples.Count) 次，不同取值 $($distinct.Count) 种：")
    $distinct | Select-Object -First 8 | ForEach-Object { Log("   $_") }
    Shot 'shell_02_running'

    # 新需求：进度窗口正文要带实时百分比（"7z 已处理...42%"）。
    # 只看进度条数值不够 —— 条在动但文字不动，用户照样看不出数字。
    $percentTexts = @($samples | Where-Object { $_ -match '[0-9]+\s*%' })
    if ($percentTexts.Count -gt 0) {
      Log("正文带百分比的采样 $($percentTexts.Count) 种：" + ($percentTexts | Select-Object -First 3) -join ' | ')
    } else {
      Log('!! 进度窗口正文没有出现百分比，需求没生效')
    }

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
    Shot 'shell_99_after'

    # 事件句柄由子进程读过后置位，映射才允许释放
    try { [void]$menu.Signal.WaitOne(5000); $menu.Signal.Dispose() } catch { }
    try { $menu.Map.Dispose() } catch { }
  }
}
finally {
  foreach ($n in @('Auto7zGUI', 'Auto7z', '7z')) {
    Get-Process $n -ErrorAction SilentlyContinue | Stop-Process -Force
  }
  Log('场景结束')
}
