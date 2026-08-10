# STS2 AutoReconnect — 重连测试与日志采集说明 / Reconnect Testing and Log Collection Guide

## 中文说明 / Chinese Instructions

### 目的 / Purpose
- 提供可复现的重连场景测试步骤与最小日志采集指南，方便定位“僵尸连接/握手超时/被误踢”等问题。
- Provide repeatable reconnect test scenarios and minimal log collection guidance for diagnosing zombie connections, handshake timeouts, and unexpected disconnects.

### 前提 / Prerequisites
- 已在目标机器部署并启用 `STS2_AutoReconnect` mod。
- The mod must be installed and enabled on both test machines.
- 有能力在主机与客机上同时操作（或两台机器）。
- You need access to both host and client machines during the test.
- 对方可能是 Windows、macOS 或 Linux，测试脚本提供 PowerShell 与 Bash 版本。
- The other tester may be on Windows, macOS, or Linux; both PowerShell and Bash helpers are included.

### 快速日志实时采集 / Quick Live Log Capture
- 实时 tail 游戏日志并写入文件（替换为实际游戏日志路径）：

```powershell
Get-Content -Path 'C:\Path\To\Game\game.log' -Wait -Tail 200 | Tee-Object -FilePath '.\reconnect_run1.log'
```

- 或者在 macOS/Linux 上：

```bash
tail -F /path/to/game.log > reconnect_run1.log
```

- 推荐分别在主机和客机上保存 `reconnect_run1_host.log` / `reconnect_run1_client.log`。
- Save separate logs on host and client for the same test window.

### 关键日志关键词 / Key Log Keywords
- 僵尸看门狗 / PeerConnectedAtMs：`僵尸看门狗|PeerConnectedAtMs|Zombie`
- 强断/豁免：`ZombieForceDisconnected|豁免强断|静默僵尸`
- 握手等待/超时：`Await\(|timeout after|超过 .* 秒|开始等待`
- 重连握手：`收到客机 .* 的重连握手请求|ClientRejoinResponseMessage|HandleRejoinResponse`
- 重连阶段/诊断：`ReconnectDiagnostics|PerformReconnect:|ResolvingTarget|Handshaking|CheckingSession`

### 优先测试用例 / Priority Test Cases
1. 被动断网 → 恢复（判定僵尸）
   - 操作：在客机上禁用网卡或断网，等待 10–30 秒，再恢复网络。
   - Expectation: host logs `PeerConnectedAtMs[...]` when transport returns, and if the client does not complete the handshake within `RejoinGraceMs`, host should log `ZombieForceDisconnected`.
   - 日志检查：`PeerConnectedAtMs`、`僵尸检测`、`ZombieForceDisconnected`

2. 主动断开后通过主机邀请从主菜单重连（MenuRejoin）
   - 操作：客机主动断开或回到主菜单，主机通过 Steam 邀请其重回对局。
   - Expectation: MenuRejoin should prefer `FromLobby` and either reconnect or provide a clear failure reason.
   - 日志检查：`ResolvingTarget`、`Connecting`、`Handshaking`、`ReportClientFailure`

3. 快速恢复网络（测试宽限期）
   - 操作：断网后快速恢复（恢复时间 < `RejoinGraceMs`，可能 > `HandshakeTimeoutSeconds`）。
   - Expectation: host should not force-disconnect during grace period, and should see `HandleRejoinResponse: received` if handshake starts.
   - 日志检查：`PeerConnectedAtMs`、`豁免强断`、`HandleRejoinResponse: received`

4. 慢网络导致握手超时（验证超时提示）
   - 操作：在客机端限制带宽或增加延迟，使握手超过 20 秒。
   - Expectation: client logs `Await(...): 开始等待，超时设置 20s` then `timeout after 20s`, and UI offers a retry button.
   - 日志检查：`Await(...): 开始等待，超时设置`、`timeout after`、`ShowManualButton`

5. 战斗中尝试重连（应得到清晰拒绝或指引）
   - 操作：在战斗中断线后尝试从菜单重连或对局内重连。
   - Expectation: if reconnect is impossible during combat, logs should show `CheckingSession` or clear rejection reason.
   - 日志检查：`sessionState != Running`、`CheckingSession`

6. 主机触发「原地保活重载」后全员自动重连回同一大厅（v0.9.0-min 根治）
   - 前置：已开启 `checkpointRollbackEnabled`，且对局中已产生过检查点（≥1 次节点存档）。
   - 操作：客机硬掉线 → 主机等 ≥15s 收到「邀请/回退」提示 → 主机在浮层点一个检查点。
   - Expectation（核心根治效果）：
     - 主机日志出现 `主机回退完成：已原地载入检查点，Steam 大厅保持不变，等待客机自动重连。`；
     - 主机与客机日志都应出现 `已拦截 NetService.Disconnect` / `CleanUp 完成且连接保活`（连接保活的关键标志）；
     - 掉线客机与被 StateDivergence 请离的在线队友都**自动重连回同一 Steam 大厅**，无需新邀请；
     - 三人最终处于同一份检查点，继续推进**不出现** `Abandoning run` 或状态分歧弹窗。
   - 日志检查：`主机检测到玩家` + `弹出邀请/回退提示`、`主机回退完成：已原地载入检查点`、`已拦截 NetService.Disconnect`、`已请客机` + `重连（回退需要整局重载）`、`HandleRejoinResponse`
   - 回归对照：v0.8.x 的旧行为是“全员回主菜单 + 主机新托管换大厅”，会导致掉线客机连旧大厅失败弹「发现未知错误」。本用例验证该问题已根除。

### 收集上传内容 / What to Upload
- `reconnect_run1_host.log`（问题时段前后各 30s）
- `reconnect_run1_client.log`（同上）
- 主观反馈：`FEEDBACK_TEMPLATE.txt` 或你们的文字说明
- 描述：问题出现时的本地时间、你所执行的操作（禁网 / 启网 / 点击重试 / 接受邀请）和角色（主机/客机）

### 网络控制辅助命令 / Network Control Helpers
- Windows:
```powershell
Get-NetAdapter | Format-Table -Auto
Disable-NetAdapter -Name "<Name>" -Confirm:$false
Enable-NetAdapter -Name "<Name>" -Confirm:$false
```
- macOS / Linux:
```bash
sudo ifconfig <adapter> down
sudo ifconfig <adapter> up
```

### 日志筛查示例 / Log Search Example
- PowerShell:
```powershell
Select-String -Path '.\reconnect_run1_host.log' -Pattern '僵尸看门狗|PeerConnectedAtMs|ZombieForceDisconnected|Await\(|HandleRejoinResponse' -Context 3,3
```
- Bash:
```bash
grep -E '僵尸看门狗|PeerConnectedAtMs|ZombieForceDisconnected|Await\(|HandleRejoinResponse' reconnect_run1_host.log -n
```

### 后续说明 / Next Steps
我会分析上传的日志并给出调整建议（例如是否需要延长 `HandshakeTimeoutSeconds` 或 `ForceDisconnectCooldownMs`）。

Please upload the test logs and feedback after the run, and I will analyze them immediately.

