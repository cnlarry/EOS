---
name: eos-read-logs
description: EOS 运行日志定位。读日志前先弄清三件事——日志写在哪个文件、什么级别才落盘、以及"界面在报错但日志查不到"时该怀疑什么。Use when troubleshooting API errors、排查 500/403/慢请求、按报障编号 correlationId 串前后端链路、或需要区分运行日志与操作审计。
---

# 读运行日志定位异常

排障顺序固定为：**文件日志 → 操作审计（在库里） → 异常堆栈**。
本文只讲第一步里最容易踩空的三件事，其余按 `docs/guide/31-运行日志与诊断.md`。

## 一、日志写在哪个文件（最容易搞错）

日志写在**运行实例的工作目录**下，不在仓库根。实例是按 `AppContext.BaseDirectory` 解析的，
所以开发态实际落在输出目录的 `logs` 子目录里（形如 `bin/Debug/net10.0/logs`）。
仓库根那个 `logs` 目录是历史遗留，早已不再写入。

按天切分，一天一份：`api-yyyyMMdd.log`；单日体量超上限时续切 `api-yyyyMMdd-2.log`。
超过 14 天（或超过 `MaxFiles` 份）自动清理。

动手前先确认实例是谁、跑的是不是最新构建：

```powershell
Invoke-RestMethod 'http://localhost:5261/health/version'   # commit / stale / processStart
Get-CimInstance Win32_Process -Filter "Name like '%EOS.API%'" | Select-Object ExecutablePath
```

`stale=$true` 表示磁盘上的二进制比运行中的新——**先让用户重启**再排障，否则你读的是旧行为。

## 二、什么才会落盘（两个条件，缺一不可）

文件日志只保留 **Warning+**。判定在 `RequestLoggingMiddleware`：

| 情况 | 级别 | 是否落文件 |
|---|---|---|
| 状态码 ≥ 500 | Error | ✅ |
| 带结构化 `error.code`（含 4xx） | Warning | ✅ |
| 慢成功（≥ 1s） | Warning | ✅ |
| 其它请求 | Information | ❌ 只进控制台 |

**`Error` 只留 5xx**：按 `Error` 过滤时可以假定"服务端有问题"——权限拒绝、参数不对这类
客户端失败是 `Warning`。要查"谁被拒了"就按 `Warning` + `ErrorCode` 过滤。

**推论（这是最常踩的坑）**：4xx 但没带 `error.code` 的请求**同样不落盘**。
所以"界面明明在报错、文件日志却一条都没有"时，**先怀疑这次失败没带错误码**，
而不是怀疑日志系统坏了。反过来，新增任何面向用户的失败出口都要问一句"它带错误码吗"。

权限拒绝（`Forbid()`）过去正是掉进这个盲区的典型，现已由 `ForbiddenProblemFilter`
统一换成带 `code=FORBIDDEN` 的 403 ProblemDetails——既能在界面读到原因，也能落盘。

## 三、日志行格式与关键字段

每行一个 JSON 对象：

```json
{"level":"Warning","category":"...","message":"...","fields":{"CorrelationId":"...","ModuleId":2306,"ErrorCode":"FORBIDDEN"},"exception":null,"ts":"...","event":"http_request"}
```

- `fields.CorrelationId` —— 报障编号，前后端串链路靠它（前端每次请求都带 `X-Correlation-Id`，服务端采用它）
- `fields.ErrorCode` —— 稳定错误码，按它 grep 最快
- `fields.ModuleId` / `fields.Action` —— 定位是哪个模块哪个动作
- `event` —— `http_request` 是请求，其它是领域事件

## 四、常用检索

读**正在被写**的那份文件时，读取方必须用 `FileShare.ReadWrite` 共享模式；
`Get-Content` / `Select-String` 已经如此，可直接用。
（若自己在 C# 里读，用 `File.ReadAllLines` 会因要求"别人不许写"而抛 IOException——
那是真实的坑，不是权限问题。）

```powershell
# 最新 N 条
Get-Content <日志文件> -Tail 50 -Encoding utf8

# 按错误码
Select-String -Path <日志目录>\api-*.log -Pattern '"ErrorCode":"FORBIDDEN"' -Encoding utf8

# 按报障编号串前后端
Select-String -Path <日志目录>\api-*.log -Pattern '<correlationId>' -Encoding utf8

# 只要 Error 级并抽关键字段
Get-Content <日志文件> -Encoding utf8 |
  Where-Object { $_ -match '"level":"(Error|Critical)"' } |
  Select-Object -Last 20
```

不想碰文件时，可用接口（需模块 `2313` 的 `CanBrowse`，先登录）：
`GET /api/v1/logs?take=200&level=Error&keyword=&correlationId=&event=&from=&file=`
——`file` 可切到某一天；响应里的 `unavailable` 是"想读但读不到"的文件，
**它与"没有日志"是两件事**，非空说明读取失败，别当成空日志。

## 五、操作审计不在日志里

"谁在什么时候对什么做了什么"在库表 `AUDIT_EVENT`（字段级变更在 `AUDIT_FIELD_CHANGE`），
**不在文件日志里**。查它有两个入口：连库直查，或 `GET /api/v1/audit`
（支持 `moduleId` / `action` / `actor` / `result` / `from` / `to` 分页，权限同为 `2313`）。

排障时两层要分开看：**运行日志回答"这次请求为什么失败"，审计回答"谁改的、改成什么"**。

## 六、三个常见误判

1. **"日志里查不到 = 没发生"** → 它很可能是 Information 级（正常请求）或 4xx 无错误码，本就不落盘。
2. **"文件有 4MB 却读不出一条"** → 读取失败被静默吞掉了；看响应里的 `unavailable`，或检查共享模式。
3. **"报障编号在文件里搜不到"** → 正常请求的编号只在控制台；确认它是否真的落盘（带错误码或 5xx 才会）。
