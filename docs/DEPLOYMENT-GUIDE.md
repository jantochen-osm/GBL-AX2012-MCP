# GBL-AX2012-MCP 部署指南（Windows）

**更新日期:** 2026-09-01  
**环境:** Windows（本机实测 OSMHZNB209，.NET SDK 8.0.424）  
**目标:** 让 MCP Server 连接 Microsoft Dynamics AX 2012 R3，实现 O2C 自动化

> **重要：项目官方仅支持 Windows 部署**（Docker 已移除，见 `docs/DOCKER-REMOVAL-COMPLETE.md`）。  
> Linux/WSL 部署时 Windows 认证不可用（`WindowsIdentity.GetCurrent()` 抛 `PlatformNotSupportedException`），所有依赖 AIF 的工具必然失败——**不要在 WSL 部署**。
>
> 前置要求：仓库代码包含运行必需修复（循环依赖、宿主生命周期）。修复已提交（commit `e161d7c`），**从 jantochen-osm/GBL-AX2012-MCP clone 后无需改源码**。若从旧版本部署，见附录 A。

---

## 目录

1. [前置条件](#1-前置条件)
2. [安装 .NET 8 SDK](#2-安装-net-8-sdk)
3. [还原依赖 & 编译](#3-还原依赖--编译)
4. [配置 AX 2012 连接](#4-配置-ax-2012-连接)
5. [启动服务器](#5-启动服务器)
6. [验证](#6-验证)
7. [实测结果（Windows 2026-09-01）](#7-实测结果windows-2026-09-01)
8. [常见问题](#8-常见问题)
9. [部署清单（Checklist）](#9-部署清单checklist)

---

## 1. 前置条件

| 依赖 | 说明 |
|------|------|
| Windows 10/11 或 Windows Server | 项目官方支持平台 |
| .NET 8.0 SDK | 编译和运行必需 |
| AX 2012 R3 | 目标系统，需要 .axc 配置获取连接信息 |
| Windows Authentication | AX 认证方式（本项目环境） |
| 运行账号的 AX 权限 | 运行服务的 Windows 账号需对 AX AIF 服务有访问权限 |

**获取 AX 连接信息：** 找到客户端 `.axc` 配置文件，用记事本/PowerShell 打开，提取关键字段：

```
aos2,Text,OSM_AX2012R3_CRP@HKS-SQLDEV07:2713   # AOS 实例 + 端口
wsdlport,Text,8102                             # WCF/WSDL 端口
wcfconfig,Text,...net.tcp://hks-sqldev07:8202... # NetTcp 端口 8202
```

---

## 2. 安装 .NET 8 SDK

**方式 A：winget（推荐）**

```powershell
winget install Microsoft.DotNet.SDK.8
```

**方式 B：官方安装包** — 下载并运行 [dotnet SDK 8.0 安装包](https://dotnet.microsoft.com/download/dotnet/8.0)

**验证：**
```powershell
dotnet --version
# 输出: 8.0.xxx
```

---

## 3. 还原依赖 & 编译

```powershell
cd D:\flexwork-space\GBL-AX2012-MCP

# 还原 NuGet 依赖
dotnet restore GBL.AX2012.MCP.sln

# 编译（只编译服务器项目；tests/ 已过时，编译报错属正常，不影响服务器）
dotnet build src\GBL.AX2012.MCP.Server\GBL.AX2012.MCP.Server.csproj
# 期望输出: 0 Error(s)（警告可忽略）
```

> 警告说明：
> - `NU1903` — System.Text.Json 8.0.0 已知高危漏洞（GHSA-8g4q-xg66-9fp4 / GHSA-hh2w-p6rv-4g7w），建议后续升级修复版本
> - `CA1416` — WindowsIdentity 平台限制，仅 Windows 可用（本项目目标即 Windows）

---

## 4. 配置 AX 2012 连接

编辑 `src\GBL.AX2012.MCP.Server\appsettings.json`：

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
| `AuditDb` | 本机/远程 SQL Server | webhook 订阅存储；本机无 SQL 时可暂留（服务可启动，webhook 工具不可用） |

> **重要：** 仓库自带的 `appsettings.json` 是示例环境（`ax-aos` 地址），部署时**必须**改为实际环境；配置完成后必须复制到输出目录（见步骤 5），否则运行时报 "configuration section is missing"。

---

## 5. 启动服务器

**先复制配置文件到输出目录：**
```powershell
Copy-Item src\GBL.AX2012.MCP.Server\appsettings.json `
          src\GBL.AX2012.MCP.Server\bin\Debug\net8.0\appsettings.json
```

**开发/验证（后台运行，身份 = 当前登录用户，Windows 认证即以此身份进行）：**
```powershell
Start-Process -FilePath "D:\flexwork-space\GBL-AX2012-MCP\src\GBL.AX2012.MCP.Server\bin\Debug\net8.0\GBL.AX2012.MCP.Server.exe" `
  -WorkingDirectory "D:\flexwork-space\GBL-AX2012-MCP\src\GBL.AX2012.MCP.Server\bin\Debug\net8.0" `
  -WindowStyle Hidden `
  -RedirectStandardOutput "D:\tmp\mcp-server.log" `
  -RedirectStandardError "D:\tmp\mcp-server-err.log"
```

> **注意：** 启动后配置验证约需 **25 秒**（AuditDb 连接超时），之后端口才就绪，属正常现象。  
> 日志：Serilog 写入 `bin\Debug\net8.0\logs\mcp-<日期>.log`（滚动 30 天）。

**生产（Windows Service，官方推荐）：**
```powershell
# 1. 发布
dotnet publish -c Release -o C:\Services\GBL-AX2012-MCP

# 2. 创建服务（管理员 PowerShell）
New-Service -Name "GBL-AX2012-MCP" `
  -BinaryPathName "C:\Services\GBL-AX2012-MCP\GBL.AX2012.MCP.Server.exe" `
  -DisplayName "GBL AX2012 MCP Server" `
  -StartupType Automatic `
  -Description "MCP Server for AX 2012 R3 integration"

# 3. 配置服务账号（需 IT 提供 DOMAIN\svc-mcp，该账号需对 AX AIF 有权限）
sc.exe config GBL-AX2012-MCP obj= "DOMAIN\svc-mcp" password= "password"

# 4. 启动
Start-Service GBL-AX2012-MCP
```

**停止服务器：**
```powershell
Stop-Process -Name GBL.AX2012.MCP.Server        # 开发实例
Stop-Service GBL-AX2012-MCP                     # Windows Service
```

---

## 6. 验证

```powershell
# Health check
curl http://localhost:8080/health
# 期望: {"status":"healthy", "aosConnected":true, ...}

# 工具列表（36 个）
curl http://localhost:8080/tools

# 调用工具
curl -X POST http://localhost:8080/tools/call `
  -H "Content-Type: application/json" `
  -d '{"tool": "ax_get_customer", "arguments": {"customerAccount": "CUST-001"}}'

# Prometheus 指标
curl http://localhost:9090/metrics
```

**端口一览：**

| 端口 | 用途 |
|------|------|
| 8080 | HTTP API (REST) |
| 9090 | Prometheus 指标 |

---

## 7. 实测结果（Windows 2026-09-01）

> 测试环境：Windows 本机 OSMHZNB209，服务器 v1.5.0，AX 连接目标 HKS-SQLDEV07

| 工具 | 结果 | 说明 |
|------|------|------|
| `ax_health_check` | ✅ 成功 | 工具链路正常 |
| `ax_get_roi_metrics` | ✅ 成功 | 本地统计 |
| `ax_get_self_healing_status` | ✅ 成功 | 熔断器状态可查 |
| `ax_get_customer` 等 AIF 工具 | ❌ `AIF_ERROR: ServiceUnavailable` | **Windows 认证已通过、请求真实到达 AIF**，AIF HTTP 8102 返回 503（服务端未就绪，需 IT 排障，见下） |
| `ax_query_audit` | ❌ `FORBIDDEN` | 预期行为：需 `MCP_Admin` 角色（角色来自 Windows 用户组） |
| `ax_list_webhooks` | ❌ `INTERNAL_ERROR` | AuditDb（SQL Server）未部署，连接失败 |

**与 WSL 部署的对比（为什么必须 Windows）：**

| 项目 | WSL (2026-08-31) | Windows (2026-09-01) |
|------|------------------|----------------------|
| `/health` | `degraded`（熔断器 open） | ✅ `healthy`（熔断器 closed） |
| 认证 | 每请求 `PlatformNotSupportedException` | ✅ 正常（Windows 身份） |
| AIF 工具错误 | `CIRCUIT_OPEN`（认证层就挂） | `AIF_ERROR: ServiceUnavailable`（真实服务端响应） |
| 结论 | AIF 工具永远不可用 | AIF 只差服务端 503，修复后即可用 |

**AIF 503 排障步骤（需服务器管理员在 HKS-SQLDEV07 执行）：**
1. 确认 AIF 应用池在 IIS 中已启动
2. 确认 AIF 虚拟目录 `/DynamicsAx` 已部署到 8102
3. 测试 WSDL：`curl http://localhost:8102/DynamicsAx/Services/SystemServiceGroup`（应返回 XML，非 503）
4. 确认服务账号对 AX 的 AIF 有权限

---

## 8. 常见问题

### Q1: 配置验证失败 "AifClient configuration section is missing"
**原因：** `appsettings.json` 未复制到 bin 输出目录。
**解决：**
```powershell
Copy-Item src\GBL.AX2012.MCP.Server\appsettings.json `
          src\GBL.AX2012.MCP.Server\bin\Debug\net8.0\appsettings.json
```

### Q2: 启动报循环依赖错误 "A circular dependency was detected"
**原因：** 旧版本代码中 `BatchOperationsTool`/`ConnectionPoolMonitor` 构造函数注入导致 DI 循环依赖。
**解决：** 已修复并提交（commit `e161d7c`）——从 jantochen-osm/GBL-AX2012-MCP clone 的代码无需处理；旧代码请应用附录 A 的修复。

### Q3: HealthMonitorService 报 TaskCanceledException 导致宿主停止
**原因：** `Task.Delay` 在取消时抛出，默认 `StopHost` 行为。
**解决：** 已修复（捕获 `OperationCanceledException` + `BackgroundServiceExceptionBehavior.Ignore`），随 commit `e161d7c` 发布。

### Q4: 服务器启动后立即退出 / 无端口监听
**可能原因 1（旧版本）：** stdio EOF 触发宿主停止（旧 `McpServer` 调用 `StopApplication`）——已修复。
**可能原因 2：** 启动后立即检查，配置验证需约 25 秒——请等待后重试。
**可能原因 3：** 端口 8080/9090 被占用（如 WSL 旧实例仍在运行）——先停止占用进程。
**解决：** 检查 `logs\mcp-<日期>.log` 中的 [FTL] 日志。

### Q5: Business Connector .NET is not installed
**说明：** 这是警告，非错误。本机未安装 BC.NET（仅 .NET Framework 可用）时使用 mock 连接。若需真实写入操作（创建订单/过账等），需部署 BC.Wrapper 服务（见 `docs/BC-WRAPPER-SETUP.md`）并设 `UseWrapper: true`。

### Q6: ax_query_audit 返回 FORBIDDEN
**说明：** 预期行为。该工具要求 `MCP_Admin` 角色，角色来自运行账号的 Windows 组（`MCP-Admins`）。如需要，请 IT 将账号加入对应 AD 组。

### Q7: ax_list_webhooks 等 webhook 工具报 INTERNAL_ERROR
**原因：** AuditDb（SQL Server）不可达。本机无 SQL Server 时 webhook 功能不可用，不影响其他工具。
**解决：** 部署 SQL Server 实例，创建 `MCP_Audit` 库并执行 EF 迁移（见 `docs/DATABASE-SETUP.md`），然后更新 `AuditDb` 连接串。

---

## 9. 部署清单（Checklist）

- [ ] 从 `https://github.com/jantochen-osm/GBL-AX2012-MCP` clone（含 commit `e161d7c` 修复）
- [ ] .NET 8 SDK 已安装（`dotnet --version` → 8.0.xxx）
- [ ] `dotnet build src\GBL.AX2012.MCP.Server\GBL.AX2012.MCP.Server.csproj` 0 错误
- [ ] `appsettings.json` 已按 .axc 配置实际环境（仓库自带为示例地址）
- [ ] `appsettings.json` 已复制到 bin 目录
- [ ] 服务器已启动（Start-Process 或 Windows Service）
- [ ] 等待约 25 秒配置验证完成
- [ ] `curl /health` 返回 healthy
- [ ] `curl /tools` 返回 36 个工具
- [ ] 日志无 `PlatformNotSupportedException`
- [ ] （生产）`New-Service` 创建 Windows Service + 服务账号已配置
- [ ] （AIF）IT 确认 AIF 应用池/虚拟目录就绪、账号有权限

---

## 附录 A：旧版本代码运行必需修复（commit e161d7c 已包含）

若从修复前的版本部署，需手工应用以下修改（修复均已合入当前仓库）：

| 文件 | 修复内容 |
|------|---------|
| `Tools/BatchOperationsTool.cs` | 构造函数不再注入 `IEnumerable<ITool>`，改为运行时 `IServiceProvider.GetServices<ITool>()` |
| `Resilience/ConnectionPoolMonitor.cs` | `ISelfHealingService` 改为 `Lazy<ISelfHealingService>` 惰性解析 |
| `McpServer.cs` | `IHostedService` → `BackgroundService`；移除 stdio EOF 时的 `StopApplication()` |
| `Program.cs` | 添加 `BackgroundServiceExceptionBehavior.Ignore` |
| `Monitoring/HealthMonitorService.cs` | `Task.Delay` 捕获 `OperationCanceledException` 并 break |
| `Tools/ToolBase.cs` | 新增 `ForbiddenException → FORBIDDEN` 错误码映射（建议） |

---

**状态:** ✅ Windows 部署完成，服务器运行正常（AIF 工具待 IT 修复服务端 503）
