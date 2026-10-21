# 进程内局域网服务与 HTTP 图片传输探针

日期：2026-10-21。依据 `docs/plans/2026-10-21-tablet-distribution.md` 第 3 节第一步。
材料：全部合成；未读取、未打印、未提交 `stories/`、`.appdata/`、`.local/` 下任何文件。

## 探针范围

| 动作 | 结果 |
|---|---|
| 同进程启动 ASP.NET Core 服务并绑定 127.0.0.1 随机端口 | 通过。端口由空闲端口分配，服务在测试进程内启停 |
| 停止服务后所有请求立即失败 | 通过。停止后连接被拒绝，无残留监听 |
| 按 cardId 经 HTTP 返回单张卡图片 | 通过。返回字节与写入内容逐字节相等 |
| 目录接口只返回公开字段 | 通过。只有 ID、名称、分组、序号，不含图片字节 |
| 未知 ID 与路径穿越 | 通过。均返回 404 |
| 6 个并发会话各自取到本人角色页 | 通过。6 页内容互不串台 |
| 重复请求字节稳定（等价刷新后重建） | 通过。5 轮结果一致 |
| Blazor Interactive Server 页面与 `_framework/blazor.web.js` | 通过。publish 产物可运行，脚本资源可达（SignalR circuit 前提） |
| 同名卡点选不串图 | 通过。两张「书房」返回不同字节 |

命令与结果（提交前实测）：

```
dotnet build LocalDm.slnx          → 0 警告 0 错误
dotnet test tests/LocalDm.Tests    → 304 总计 / 298 通过 / 6 跳过（EdgeTtsTests 已授权）/ 0 失败
dotnet test tests/LocalDm.UiTests  → 33 通过 / 0 失败
```

## 端口与防火墙提示（实测）

- 探针只绑定 `127.0.0.1`，未触发 Windows 防火墙提示。
- 真实局域网分发需绑定所选局域网 IPv4；首次绑定时 Windows 防火墙会提示，需要用户在「专用网络」放行本应用。本报告未在真实网络环境验证该提示，标记**未执行**。

## 代码位置

- `src/LocalDm.Tablet/`：合成最小服务（Blazor Interactive Server + 图片 HTTP 接口），不是产品实现。
- `tests/LocalDm.Tests/TabletServiceSpikeTests.cs`、`TabletConcurrencySpikeTests.cs`、`TabletBlazorSpikeTests.cs`、`TabletBlazorHost.cs`。

## 未执行项

- WPF 进程内启停（当前由测试进程与子进程验证，尚未接入 `AppComposition`）。
- 局域网 IPv4 绑定、防火墙提示与「连不上怎么办」指引。
- 真实设备上的加入、阅读、缩放、横竖屏、切后台与锁屏恢复。
