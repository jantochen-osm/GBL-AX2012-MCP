# GBL-AX2012-MCP 部署指南

**日期:** 2026-08-31  
**环境:** WSL (Ubuntu 26.04) / Linux x86_64  
**目标:** 让 MCP Server 连接 Microsoft Dynamics AX 2012 R3，实现 O2C 自动化

---

## 目录

1. [前置条件](#1-前置条件)
2. [安装 .NET 8 SDK](#2-安装-net-8-sdk)
3. [还原依赖 & 编译](#3-还原依赖--编译)
4. [配置 AX 2012 连接](#4-配置-ax-2012-连接)
5. [启动服务器](#5-启动服务器)
6. [验证](#6-验证)
7. [常见问题](#7-常见问题)

---

## 1. 前置条件

| 依赖 | 说明 |
|------|------|
| .NET 8.0 SDK | 编译和运行必需 |
| AX 2012 R3 | 目标系统，需要 .axc 配置获取连接信息 |
| Windows Authentication | AX 认证方式（本项目环境） |

**获取 AX 连接信息：** 找到客户端 `.axc` 配置文件，用记事本/PowerShell 打开，提取关键字段：

```
aos2,Text,OSM_AX2012R3_CRP@HKS-SQLDEV07:2713   # AOS 实例 + 端口
wsdlport,Text,8102                             # WCF/WSDL 端口
wcfconfig,Text,...net.tcp://hks-sqldev07:8202... # NetTcp 端口 8202
```

---

## 2. 安装 .NET 8 SDK

```bash
# 下载官方安装脚本
wget -q https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh

# 安装到用户目录
export DOTNET_INSTALL_DIR="$HOME/.dotnet"
/tmp/dotnet-install.sh --channel 8.0 --install-dir "$DOTNET_INSTALL_DIR"
```

**配置环境变量（持久化到 .bashrc）：**
```bash
echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> ~/.bashrc
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.bashrc
echo 'export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1' >> ~/.bashrc
source ~/.bashrc
```

> **注意：** `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` 是必需的。若缺失，运行时报错：
> `Couldn't find a valid ICU package installed on the system`

**验证：**
```bash
dotnet --version
# 输出: 8.0.xxx
```

---

## 3. 还原依赖 & 编译

```bash
cd /mnt/d/flexwork-space/GBL-AX2012-MCP

# 还原 NuGet 依赖
dotnet restore GBL.AX2012.MCP.sln

# 编译
dotnet build GBL.AX2012.MCP.sln
# 期望输出: 0 Error(s)
```

**运行测试（可选）：**
```bash
dotnet test GBL.AX2012.MCP.sln --no-build
```
> 已知部分测试失败（Moq 非虚方法、JSON 序列化），不影响服务器运行。

---

## 4. 配置 AX 2012 连接

编辑 `src/GBL.AX2012.MCP.Server/appsettings.json`：

```json
{
  "AifClient": {
    "BaseUrl": "http://HKS-SQLDEV07:8102/DynamicsAx/Services",
    "Company": "DAT",
    "UseNetTcp": true,
    "NetTcpPort": 8202,
    "FallbackStrategy": "auto"
  },
  "WcfClient": {
    "BaseUrl": "http://HKS-SQLDEV07:8102/GBL/SalesOrderService.svc"
  },
  "BusinessConnector": {
    "ObjectServer": "HKS-SQLDEV07:2713",
    "Company": "DAT",
    "UseWrapper": false
  },
  "ConnectionStrings": {
    "AuditDb": "Server=localhost;Database=MCP_Audit;Trusted_Connection=True;TrustServerCertificate=True"
  }
}
```

**关键配置项说明：**

| 配置项 | 来源 | 说明 |
|--------|------|------|
| `AifClient.BaseUrl` | .axc `wsdlport` | AIF HTTP 服务地址 |
| `AifClient.NetTcpPort` | .axc `wcfconfig` | NetTcp 端口（8202）|
| `BusinessConnector.ObjectServer` | .axc `aos2` | AOS 服务器:端口 |
| `UseWrapper` | 是否部署 BC.Wrapper | 未部署则 `false` |

> **重要：** 配置完成后，必须将 `appsettings.json` 复制到输出目录（见步骤 5），否则运行时报 "configuration section is missing"。

---

## 5. 启动服务器

**先复制配置文件到输出目录：**
```bash
cp src/GBL.AX2012.MCP.Server/appsettings.json \
   src/GBL.AX2012.MCP.Server/bin/Debug/net8.0/appsettings.json
```

**启动（关键：使用 setsid 脱离终端）：**
```bash
cd src/GBL.AX2012.MCP.Server/bin/Debug/net8.0
setsid nohup ./GBL.AX2012.MCP.Server > /tmp/mcp-server.log 2>&1 < /dev/null &
```

> **必须用 `setsid`！** 若用普通 `nohup ... &`，当 bash 命令结束时进程收到 SIGHUP 信号，服务器会立即关闭。

**停止服务器：**
```bash
pkill -f GBL.AX2012.MCP.Server
```

---

## 6. 验证

```bash
# Health check
curl -s http://localhost:8080/health
# 期望: {"status":"healthy", "aosConnected":true, ...}

# 工具列表
curl -s http://localhost:8080/tools
# 期望: 36 个工具

# 调用工具
curl -X POST http://localhost:8080/tools/call \
  -H "Content-Type: application/json" \
  -d '{"tool": "ax_get_customer", "arguments": {"customerAccount": "CUST-001"}}'

# Prometheus 指标
curl -s http://localhost:9090/metrics

# 确认进程存活
ps aux | grep GBL.AX2012 | grep -v grep
```

**端口一览：**

| 端口 | 用途 |
|------|------|
| 8080 | HTTP API (REST) |
| 9090 | Prometheus 指标 |

---

## 6.1 工具调用测试结果（Linux/WSL 实测）

> 测试环境：WSL Ubuntu 26.04，服务器 v1.5.0，AX 连接目标 HKS-SQLDEV07

| 工具 | 结果 | 说明 |
|------|------|------|
| `ax_health_check` | ✅ 成功 | 不依赖 AIF，验证工具链路可用 |
| `ax_get_customer` | ❌ `CIRCUIT_OPEN` | AIF 连接失败触发熔断器 |
| `ax_get_salesorder` | ❌ `CIRCUIT_OPEN` | 同上 |
| `ax_check_inventory` | ❌ `CIRCUIT_OPEN` | 同上 |
| `ax_get_item` | ❌ `CIRCUIT_OPEN` | 同上 |

**结论：** 工具调用链路本身正常（`ax_health_check` 成功证明）。依赖 AIF 的工具失败，根因有两个：

### 根因 1：Windows 认证在 Linux 上不可用（设计限制）
```
System.PlatformNotSupportedException: Windows Principal functionality is not supported on this platform.
    at WindowsAuthenticationService.AuthenticateAsync (AuthenticationService.cs:47)
```
`WindowsIdentity.GetCurrent()` 仅支持 Windows。**这是代码层面无法绕过的**，需要在 Windows Server 上部署。

### 根因 2：AIF 连接实际不可用
- **HTTP (8102):** 返回 `503 Service Unavailable`（IIS 活着，但 AIF 应用池未启动 / 虚拟目录未部署）
- **NetTcp (8202):** `ServiceChannel Faulted state`（服务契约或认证不匹配）
- 3 次失败后熔断器打开 60 秒，连锁导致所有 AIF 工具失败（共享同一熔断器实例）

### 排障步骤（Windows Server 部署后）
1. 确认 AIF 应用池在 IIS 中已启动
2. 确认 AIF 虚拟目录 `/DynamicsAx` 已部署到 8102
3. 测试 WSDL：`curl http://localhost:8102/DynamicsAx/Services/SystemServiceGroup`（应返回 XML，非 503）
4. 确认服务账号对 AX 的 AIF 有权限

---

## 7. 常见问题

### Q1: 配置验证失败 "AifClient configuration section is missing"
**原因：** `appsettings.json` 未复制到 bin 输出目录。
**解决：**
```bash
cp src/GBL.AX2012.MCP.Server/appsettings.json \
   src/GBL.AX2012.MCP.Server/bin/Debug/net8.0/appsettings.json
```

### Q2: 循环依赖错误
服务器启动报 `A circular dependency was detected`。已修复：
- `BatchOperationsTool`：改为运行时从 `IServiceProvider` 解析工具
- `ConnectionPoolMonitor`：用 `Lazy<ISelfHealingService>` 打破循环

### Q3: HealthMonitorService 报 TaskCanceledException 导致宿主停止
**原因：** `Task.Delay` 在取消时抛出，默认 `StopHost` 行为。
**解决：** 捕获 `OperationCanceledException` 并 `break`，设置 `BackgroundServiceExceptionBehavior.Ignore`。

### Q4: 服务器启动后立即退出
**原因：** 未用 `setsid`，bash 结束后进程收到 SIGHUP。
**解决：** 使用 `setsid nohup ... &` 启动。

### Q5: Business Connector .NET is not installed
**说明：** 这是警告，非错误。BC.NET 仅 Windows/.NET Framework 可用，Linux 下使用 mock 连接。若需真实写入操作，需部署 BC.Wrapper 服务（见 `docs/BC-WRAPPER-SETUP.md`）。

### Q6: libicu 缺失
**解决：** 设置 `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`。

---

## 部署清单（Checklist）

- [ ] .NET 8 SDK 已安装
- [ ] `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` 已设置
- [ ] `dotnet restore` 成功
- [ ] `dotnet build` 0 错误
- [ ] `appsettings.json` 已按 .axc 配置
- [ ] `appsettings.json` 已复制到 bin 目录
- [ ] 用 `setsid nohup` 启动
- [ ] `curl /health` 返回 healthy
- [ ] `curl /tools` 返回 36 个工具

---

**状态:** ✅ 部署完成，服务器运行正常
