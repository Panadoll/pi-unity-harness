# 聚焦 Unity 窗口并可选发送 Ctrl+R（触发 Unity 自身 Refresh）
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [switch]$Refresh
)

Add-Type -Namespace Pi -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
'@

$KEYUP = 2
$p = Get-Process -Id $ProcessId -ErrorAction Stop
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { Write-Output "no-main-window"; exit 1 }

# ALT 轻敲解除前台锁，然后附加到前台线程再 SetForegroundWindow
[Pi.Win]::keybd_event(0x12, 0, 0, [System.UIntPtr]::Zero)
[Pi.Win]::keybd_event(0x12, 0, $KEYUP, [System.UIntPtr]::Zero)
[void][Pi.Win]::ShowWindow($h, 9)

$fg = [Pi.Win]::GetForegroundWindow()
$fgPid = 0
$fgThread = [Pi.Win]::GetWindowThreadProcessId($fg, [ref]$fgPid)
$curThread = [Pi.Win]::GetCurrentThreadId()
$attached = $false
if ($fgThread -ne $curThread) { $attached = [Pi.Win]::AttachThreadInput($curThread, $fgThread, $true) }
$ok = [Pi.Win]::SetForegroundWindow($h)
if ($attached) { [void][Pi.Win]::AttachThreadInput($curThread, $fgThread, $false) }
Start-Sleep -Milliseconds 300

if ($Refresh) {
    [Pi.Win]::keybd_event(0x11, 0, 0, [System.UIntPtr]::Zero)        # CTRL down
    Start-Sleep -Milliseconds 60
    [Pi.Win]::keybd_event(0x52, 0, 0, [System.UIntPtr]::Zero)        # R down
    [Pi.Win]::keybd_event(0x52, 0, $KEYUP, [System.UIntPtr]::Zero)   # R up
    Start-Sleep -Milliseconds 60
    [Pi.Win]::keybd_event(0x11, 0, $KEYUP, [System.UIntPtr]::Zero)   # CTRL up
    Write-Output "sent-ctrl-r focused=$ok pid=$ProcessId"
} else {
    Write-Output "focused=$ok pid=$ProcessId"
}
