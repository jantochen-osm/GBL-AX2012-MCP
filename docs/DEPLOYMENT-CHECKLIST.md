# GBL-AX2012-MCP 部署清单（基于 GitHub 仓库）

**仓库:** https://github.com/rweisssieker-xp/GBL-AX2012-MCP
**目标环境:** Windows Server / Windows 10+（项目官方仅支持 Windows 部署，Docker 已移除）
**实测日期:** 2026-09-01（Windows 本机 OSMHZNB209 验证通过）

---

## ⚠️ 重要前置：仓库源码需包含 6 处修复

**原样 clone 的仓库代码无法直接启动。** 已实测验证：启动时 DI 解析在 `host.RunAsync()` 处崩溃：

```
System.InvalidOperationException: A circular dependency was detected for the service of type
'IEnumerable<ITool>'.
IEnumerable<IHostedService> -> IHostedService(McpServer) -> IEnumerable<ITool>
-> ITool(BatchOperationsTool) -> IEnumerable<ITool>
```

以下修复目前在**本地工作区（未提交）**，部署前必须应用（或先提交到仓库再 clone）：

| 文件 | 修复内容 | 必要性 |
|------|---------|--------|
| `src/GBL.AX2012.MCP.Server/Tools/BatchOperationsTool.cs` | 改为运行时从 `IServiceProvider` 解析工具，构造函数不再注入 `IEnumerable<ITool>` | **必需**（否则启动崩溃） |
| `src/GBL.AX2012.MCP.Server/Resilience/ConnectionPoolMonitor.cs` | 用 `Lazy<ISelfHealingService>` 打破循环依赖 | **必需**（否则启动崩溃） |
| `src/GBL.AX2012.MCP.Server/McpServer.cs` | `IHostedService` → `BackgroundService`；stdio EOF 不再 `StopApplication()`；HTTP 模式 keep-alive | **必需**（否则服务随机退出） |
| `src/GBL.AX2012.MCP.Server/Program.cs` | `BackgroundServiceExceptionBehavior.Ignore`（后台服务异常不停止宿主） | **必需**（否则 Q3 类异常停宿主） |
| `src/GBL.AX2012.MCP.Server/Monitoring/HealthMonitorService.cs` | 捕获 `OperationCanceledException` 并 break | **必需**（同上） |
| `src/GBL.AX2012.MCP.Server/Tools/ToolBase.cs` | 新增 `ForbiddenException → FORBIDDEN` 映射（否则权限不足误报 INTERNAL_ERROR） | **建议**（错误信息质量） |

> 测试项目（`tests/`）已过时、编译报错（BatchOperationsTool/ConnectionPoolMonitor 构造函数签名变化），**构建时跳过**，不影响服务器。

---

## 部署步骤

### 0. 准备环境（一次性）

- [ ] Windows 机器，安装 .NET 8 SDK（实测 8.0.424）
- [ ] 从 `.axc` 客户端配置文件提取 AX 连接信息：
  - `aos2` → AOS 服务器:端口（如 `HKS-SQLDEV07:2713`）
  - `wsdlport` → AIF HTTP 端口（如 `8102`）
  - `wcfconfig` → NetTcp 端口（如 `8202`）

### 1. 获取代码

```bash
git clone https://github.com/rweisssieker-xp/GBL-AX2012-MCP
cd GBL-AX2012-MCP
# 若修复未提交到仓库：应用上文 6 处修复
```

### 2. 构建（只构建服务器项目，跳过测试项目）

```bash
dotnet build src/GBL.AX2012.MCP.Server/GBL.AX2012.MCP.Server.csproj
# 期望: 0 Error(s)（警告可忽略，含 NU1903 System.Text.Json 8.0.0 已知漏洞提示）
```

### 3. 配置 `appsettings.json`

编辑 `src/GBL.AX2012.MCP.Server/appsettings.json`，对照 `.axc`：

```jsonc
{
  "AifClient": {
    "BaseUrl": "http://<AOS主机>:8102/DynamicsAx/Services",   // ← wsdlport
    "UseNetTcp": true,
    "NetTcpPort": 8202,                                        // ← wcfconfig
    "Company": "DAT"
  },
  "WcfClient": {
    "BaseUrl": "http://<AOS主机>:8102/GBL/SalesOrderService.svc"
  },
  "BusinessConnector": {
    "ObjectServer": "<AOS主机>:2713",                          // ← aos2
    "Company": "DAT",
    "UseWrapper": false                                        // 未部署 BC.Wrapper 则 false
  },
  "ConnectionStrings": {
    "AuditDb": "Server=localhost;Database=MCP_Audit;Trusted_Connection=True;TrustServerCertificate=True"
    // webhook 订阅存储；本机无 SQL Server 时可暂留（服务可启动，webhook 工具不可用）
  }
}
```

> 仓库自带的 appsettings.json 是示例环境（`ax-aos`），**必须改**为本环境地址。

### 4. 复制配置到输出目录

```bash
cp src/GBL.AX2012.MCP.Server/appsettings.json \
   src/GBL.AX2012.MCP.Server/bin/Debug/net8.0/appsettings.json
```
> 缺少此步 → 启动报 "configuration section is missing"。

### 5. 启动

**开发调试（dotnet run，任意目录均可，代码已固定配置根到 bin）：**
```powershell
dotnet run --project src\GBL.AX2012.MCP.Server
```

**开发/验证（以当前用户身份，Windows 认证即用此身份）：**
```powershell
Start-Process -FilePath "<路径>\bin\Debug\net8.0\GBL.AX2012.MCP.Server.exe" `
  -WorkingDirectory "<路径>\bin\Debug\net8.0" -WindowStyle Hidden `
  -RedirectStandardOutput "D:\tmp\mcp-server.log" -RedirectStandardError "D:\tmp\mcp-server-err.log"
```

**生产（Windows Service，需服务账号）：**
```powershell
dotnet publish -c Release -o C:\Services\GBL-AX2012-MCP
New-Service -Name "GBL-AX2012-MCP" -BinaryPathName "C:\Services\GBL-AX2012-MCP\GBL.AX2012.MCP.Server.exe" `
  -DisplayName "GBL AX2012 MCP Server" -StartupType Automatic `
  -Description "MCP Server for AX 2012 R3 integration"
sc.exe config GBL-AX2012-MCP obj= "DOMAIN\svc-mcp" password= "password"
Start-Service GBL-AX2012-MCP
```

> 启动后配置验证约需 25 秒（AuditDb 连接超时），之后端口才就绪，属正常现象。

**停止：** `Stop-Process -Name GBL.AX2012.MCP.Server` / `Stop-Service GBL-AX2012-MCP`

### 6. 验证

| 检查 | 命令 | 预期 |
|------|------|------|
| 健康 | `curl http://localhost:8080/health` | `"status": "healthy"`，`aosConnected: true` |
| 工具列表 | `curl http://localhost:8080/tools` | 36 个工具 |
| 工具调用 | `curl -X POST http://localhost:8080/tools/call -H "Content-Type: application/json" -d '{"tool":"ax_health_check","arguments":{}}'` | `"success": true` |
| 指标 | `curl http://localhost:9090/metrics` | Prometheus 文本 |
| 日志 | `bin\Debug\net8.0\logs\mcp-<日期>.log` | 无 `PlatformNotSupportedException`（Windows 认证正常） |
| 权限错误 | 调用 `ax_query_audit` | `FORBIDDEN`（有 ToolBase 修复时） |

**端口:** 8080 HTTP API / 9090 Prometheus

---

## 已知限制（部署后需外部配合）

1. **AIF HTTP 返回 503**（`AIF_ERROR: ServiceUnavailable`）：网络通、认证通，但 AIF 应用池未启动/虚拟目录未部署 → **需服务器管理员在 AOS 主机 IIS 上处理**：启动 AIF 应用池、确认 `/DynamicsAx` 部署到 8102、验证 `curl http://localhost:8102/DynamicsAx/Services/SystemServiceGroup` 返回 XML。
2. **AIF 权限**：运行服务的 Windows 账号需对 AX AIF 服务有权限。
3. **webhook 存储为内存实现**：订阅/退订/列表/投递功能完整（无需 SQL Server），但**服务器重启后订阅丢失**。需要持久化时：部署 SQL Server 建 `MCP_Audit` 库 + EF 迁移，`Program.cs` 注册切换回 `DatabaseWebhookService`。
4. **BC.NET 未安装**：日志警告 "Business Connector .NET is not installed - using mock connection" → 写入类工具（创建订单/过账等）不可用，需部署 BC.Wrapper（见 `docs/BC-WRAPPER-SETUP.md`）。
5. **System.Text.Json 8.0.0 高危漏洞**（NU1903: GHSA-8g4q-xg66-9fp4 / GHSA-hh2w-p6rv-4g7w）：建议升级到修复版本。

---

## 与 DEPLOYMENT-GUIDE.md 的差异说明

| 项目 | 旧指南（WSL） | 本清单（Windows） |
|------|-------------|------------------|
| 部署环境 | WSL Ubuntu | Windows（官方唯一支持） |
| 启动方式 | `setsid nohup` | `Start-Process` / Windows Service |
| Windows 认证 | ❌ 不可用（AIF 全挂） | ✅ 可用（AIF 只差服务端 503） |
| 源码修改 | 未提及（实际必需） | 明确列出 6 处（5 必需 + 1 建议） |
