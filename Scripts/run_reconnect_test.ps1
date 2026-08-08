<#
STS2 AutoReconnect — 自动化重连测试脚本（PowerShell）

用途：在主机/客机上分别运行，用于自动采集日志、执行断网/恢复操作并打包日志供分析。

用法示例（在客机/主机分别运行）：
PowerShell -ExecutionPolicy Bypass -File .\run_reconnect_test.ps1 -Role client -LogFile 'C:\Path\To\Game\game.log' -AdapterName 'Wi-Fi' -DisableSeconds 12

参数：
-Role: 'host' 或 'client'（仅用于命名输出目录）
-LogFile: 要 tail 的游戏日志文件路径（默认示例值，请替换）
-AdapterName: 要禁用/启用的网卡名（可选；如果省略脚本只做日志采集）
-DisableSeconds: 禁网持续秒数（默认 10）
-PreWait: 在执行禁网前等待秒数，便于准备（默认 5）
-OutDir: 指定输出目录（默认基于时间戳生成）

说明：脚本会在后台以 Job 方式运行 `Get-Content -Wait | Tee-Object` 将游戏日志实时写入输出目录下的 log 文件，记录操作时间戳，自动禁用/启用网卡（如果提供了网卡名），最后停止后台 job 并将输出目录压缩为 zip 文件。
#>
param(
    [ValidateSet('host','client')]
    [string]$Role = 'client',
    [string]$LogFile = 'C:\Path\To\Game\game.log',
    [string]$AdapterName = '',
    [int]$DisableSeconds = 10,
    [int]$PreWait = 5,
    [string]$OutDir = "reconnect_test_{0}" -f (Get-Date -Format 'yyyyMMdd_HHmmss')
)

# 创建输出目录
$fullOut = Join-Path -Path (Get-Location) -ChildPath $OutDir
New-Item -Path $fullOut -ItemType Directory -Force | Out-Null

# 输出文件路径
$logOut = Join-Path $fullOut "$Role`_game.log"
$actionsOut = Join-Path $fullOut "$Role`_actions.log"
$metaOut = Join-Path $fullOut "$Role`_meta.txt"

# 记录元信息
"Role: $Role" | Out-File -FilePath $metaOut -Encoding UTF8
"Started: $(Get-Date -Format o)" | Out-File -FilePath $metaOut -Encoding UTF8 -Append
"LogFile: $LogFile" | Out-File -FilePath $metaOut -Encoding UTF8 -Append
"AdapterName: $AdapterName" | Out-File -FilePath $metaOut -Encoding UTF8 -Append
"DisableSeconds: $DisableSeconds" | Out-File -FilePath $metaOut -Encoding UTF8 -Append

Write-Output "[Test] 输出目录： $fullOut"
Write-Output "[Test] 即将开始日志采集（如果 LogFile 可读）..."

# 启动日志 tail 后台 job（若日志文件存在）
$job = $null
if (Test-Path $LogFile) {
    Write-Output "[Test] 启动后台日志采集 -> $logOut"
    $logFileParam = $LogFile
    $outParam = $logOut
    $job = Start-Job -ScriptBlock {
        param($src, $dst)
        try {
            Get-Content -Path $src -Wait -Tail 200 | Tee-Object -FilePath $dst
        }
        catch {
            "[Job] 日志采集失败: $($_.Exception.Message)" | Out-File -FilePath $dst -Append
        }
    } -ArgumentList $logFileParam, $outParam
    Start-Sleep -Seconds 1
} else {
    "[Test] 指定的 LogFile 不存在：$LogFile。仅记录动作，不采集游戏日志。" | Out-File -FilePath $logOut -Encoding UTF8
}

function Stamp([string]$msg) {
    $t = Get-Date -Format o
    "$t`t$msg" | Out-File -FilePath $actionsOut -Append -Encoding UTF8
    Write-Output "[$t] $msg"
}

# 等待准备
Stamp "PreWait: 等待 $PreWait 秒以便准备..."
Start-Sleep -Seconds $PreWait

if ($AdapterName -ne '') {
    # 禁用网卡
    Stamp "Disable adapter: $AdapterName"
    try {
        Disable-NetAdapter -Name $AdapterName -Confirm:$false -ErrorAction Stop
        Stamp "Adapter disabled"
    }
    catch {
        Stamp "Disable-NetAdapter failed: $($_.Exception.Message)"
    }

    Stamp "Sleeping for $DisableSeconds seconds (network down)"
    Start-Sleep -Seconds $DisableSeconds

    Stamp "Enable adapter: $AdapterName"
    try {
        Enable-NetAdapter -Name $AdapterName -Confirm:$false -ErrorAction Stop
        Stamp "Adapter enabled"
    }
    catch {
        Stamp "Enable-NetAdapter failed: $($_.Exception.Message)"
    }
} else {
    Stamp "AdapterName not provided — 跳过禁网步骤，仅采集日志。"
}

# Give some time to let logs flush
Stamp "Post-wait: 等待 8 秒以便日志刷新"
Start-Sleep -Seconds 8

# 停止后台 job 并保存
if ($job -ne $null) {
    Stamp "Stopping log job (Id=$($job.Id))"
    try {
        Stop-Job -Job $job -Force -ErrorAction SilentlyContinue
        Receive-Job -Job $job -ErrorAction SilentlyContinue | Out-Null
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }
    catch {
        Stamp "StopJob/RemoveJob 出错：$($_.Exception.Message)"
    }
}

Stamp "Test finished. Packaging results..."

# 打包成 zip
$zipPath = Join-Path (Get-Location) "$OutDir.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
try {
    Compress-Archive -Path $fullOut\* -DestinationPath $zipPath -Force
    Stamp "Packaged logs -> $zipPath"
}
catch {
    Stamp "Compress-Archive 失败：$($_.Exception.Message)"
}

Stamp "Completed: $(Get-Date -Format o)"
Write-Output "输出已保存到: $fullOut ; 打包: $zipPath"
