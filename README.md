# TCPStudio

TCP/UDP 调试工作台（桌面应用）：多连接管理、实时收发、HEX/ASCII/UTF-8/Base64 编解码、预设数据包。基于 **WPF + WebView2 + React** 混合架构实现。

## 功能特性

- **连接管理**：多套 TCP 客户端连接配置的增删改查，持久化 JSON 存储（`Data/`）
- **实时收发**：连接/断开、数据收发流展示（原始字节 HEX / 解码文本双视图）、500 条内存级滚动窗口
- **编码体系**：HEX / ASCII / UTF-8 / Base64，发送编码跟随连接配置；解码视图预留编解码插件接入点
- **预设包**：常用报文一键填入发送区，绑定归属连接
- **状态推送**：连接状态变化、收到数据由宿主实时推送至前端渲染

## 技术架构

```
┌─────────────────────────────────────────────┐
│  TCPStudio (WPF, net10.0-windows)           │
│  ┌─────────────┐      ┌──────────────────┐  │
│  │ TcpService   │◄────►│  ShellWindowBase │  │
│  │ (业务服务)    │      │  (WPF.Web 外壳)   │  │
│  └──────┬──────┘      └────────┬─────────┘  │
└───────────────────────────────┼────────────┘
     SmartInvoker            HostBridge
    （前端→后端 调用）      （后端→前端 调用）
┌─────────┴──────────────────────┴────────────┐
│  WebView2 (Chromium 内核)                    │
│  ┌────────────────────────────────────────┐ │
│  │ Web (React 19 + Semi UI + Vite)        │ │
│  │ Common.jsx：桥接层 / Api.js：业务代理    │ │
│  └────────────────────────────────────────┘ │
└─────────────────────────────────────────────┘
```

## 目录结构

| 目录 | 说明 |
|---|---|
| `TCPStudio/` | WPF 主程序：业务服务（`Services/`）+ 外壳配置（`MainWindow`） |
| `WPF.Web/` | **实验核心 ①**：WPF + WebView2 外壳 NuGet 包（详见其 [README](WPF.Web/README.md)） |
| `Web/` | React 前端工程，`src/Common/Common.jsx` 为 **实验核心 ②** |
| `TcpTestServer/` | TCP 回环测试服务端（监听 8080、自动回显、群发/随机推送），联调用 |
| `BridgeTest/` | 桥接链路回归测试（84 项断言，不加入主解决方案，避免干扰常规构建） |
| `Releases/` | 构建输出目录（`npm run build` 产物，不入库） |

## 快速开始

```bash
# 环境：.NET 10 SDK、Node 18+、WebView2 Evergreen 运行时

# 开发模式（前端热更新 + WPF 宿主加载 dev server）
npm run web                                   # 终端 1：Vite dev server :3000
dotnet run --project TCPStudio                # 终端 2：F5 亦可，加载 localhost:3000

# 发布构建（WPF publish + Web 产物对齐到 Releases）
npm run build

# 联调测试服务端
dotnet run --project TcpTestServer            # 默认 127.0.0.1:8080

# 桥接回归测试（独立载体，不在 slnx 中）
dotnet run --project BridgeTest
```

---

## 这是一个实验性项目

TCPStudio 的功能（TCP 调试）只是载体，**真正的目的是验证 "WPF + WebView2 + 前端渲染" 实现桌面应用的完整开发方案**——即：用 Web 技术栈（React + 组件库）承担全部 UI，WPF 仅作为原生宿主提供窗口外壳与系统能力，两者通过自研双向桥接层通信。

实验结论沉淀为两个可复用资产：`WPF.Web`（C# 侧）与 `Common.jsx`（JS 侧）。它们不包含任何 TCP 业务代码，可整体移植到任何 WPF + WebView2 项目。

### 实验要回答的问题

1. 前端如何**直接传强类型对象**调用 C# 方法，两侧都不手写 JSON 序列化/反序列化？
2. 后端如何**主动调用前端方法并拿到返回值**（请求-响应，而非单向推送）？
3. 桥接层如何兼容常见传参形态与数据类型，异常如何穿透到对端？
4. 无边框窗口、WebView2 安全配置、开发/发布双模式页面加载，能否封装为统一外壳？

### 实验核心 ①：`WPF.Web`（C# 宿主侧）

WPF + WebView2 外壳模板 NuGet 包（`net10.0-windows7.0`），消费方窗口继承 `ShellWindowBase` 即得全部能力：

| 组件 | 职责 | 验证点 |
|---|---|---|
| `ShellWindowBase` | 无边框窗口 + 顶栏按钮 + WebView2 生命周期，样式由 `Themes/Generic.xaml` 提供 | 外壳与业务彻底分离，消费方 XAML 零布局代码 |
| `WebShellOptions` | 配置对象：宿主对象字典、DEBUG dev server 地址、Release wwwroot 虚拟主机映射 | 单一 Options 入口定制，无需覆写方法；进阶可覆写 4 个虚方法 |
| `Common/SmartInvoker` | **前端 → 后端**桥接：反射扫描业务服务公开方法，统一入口 `InvokeCall(方法名, 入参)` | 强类型 POCO 接参/返回；兼容裸文本/JSON 标量/对象/数组/多参/重载/可空/枚举/Guid/DateTime；`Task<T>` 自动解包；异常解包穿透；camelCase 契约 |
| `Common/HostBridge` | **后端 → 前端**桥：`ExecuteScriptWithResultAsync` 调用前端订阅总入口 | 通知（不等结果）与请求-响应（`CallAsync<T>` 等待返回值）双模式；超时兜底；任意线程可调；无注入面 |

**SmartInvoker 设计要点**（前端→后端）：

```csharp
// 业务服务按普通 C# 写，单参数约定（基础类型或 POCO）：
public SendResult Send(SendRequest req) { ... }
// 注册即生效：
Options.HostObjects.Add("Tcp", new SmartInvoker(_tcp));
```

- 序列化职责全部下沉桥接层：入参 JSON → 强类型绑定（camelCase + 大小写不敏感），返回值自动序列化；
- bool 返回输出 JSON 字面量 `true/false`，规避历史 `"True"` 字符串真值坑；
- 桥接调用发生在 UI 线程：阻塞 IO 须 `Task.Run` 调度线程池（`TcpService.Send` 为示范）。

**HostBridge 设计要点**（后端→前端）：

```csharp
Bridge.CallAsync("data.received", record);          // 单向通知
var ok = await Bridge.CallAsync<bool>("showConfirm", d); // 请求-响应
```

- 曾实验过 `PostWebMessage + callId 回执` 方案（TCS 挂起表 + Rpc HostObject），最终验证 `ExecuteScriptWithResultAsync`（JS 返回的 Promise 被内核自动 await）链路更短、代码量减半，**仅保留后者**；
- 约束：`CallAsync<T>` 不得在 UI 线程上 await（脚本求值依赖 UI 消息循环，同线程等待死锁）；后台线程调用无限制。

### 实验核心 ②：`Web/src/Common/Common.jsx`（JS 桥接侧）

与 `WPF.Web` 一一对应的**前端公用桥接层**，不含任何业务代码（业务代理在同目录 `Api.js`）：

| 导出 | 职责 |
|---|---|
| `InvProxy` | **前端 → 后端**：两层 Proxy 把 `Tcp.Send({id,data})` 透明转换为 `hostObjects.Tcp.InvokeCall("Send", json)`；对象自动 stringify、结果自动 parse（JSON/数字识别） |
| `host`（`HostActionBind`） | **后端 → 前端**：`window.hostRpc.onHostAction(action, data)` 订阅总入口 + `host.bind(action, fn)` / `host.unBind(...actions)` 路由注册表；handler 返回值即后端 `CallAsync<T>` 拿到的结果；未绑定 action 显式抛错防静默丢失 |
| `withRouter` / `getGuid` | 通用工具（路由 HOC、GUID） |

```js
// 业务侧全部用法只有两类：
const list = await Tcp.GetConnections();          // 调后端，直接传/收对象
host.bind('connection.status', () => this.reload()); // 订阅后端推送，返回即回执
```

**设计原则**（实验得出）：

1. **业务代码零 JSON 感知**——序列化/反序列化、字段命名（camelCase）契约由桥接层两端各自兑现；
2. **总入口模式**——后端调前端只有一个函数 `onHostAction(action, data)`，action 路由表归业务自管，桥接层不预设任何 action 语义；
3. **失败必须可见**——未注册方法抛异常、未绑定 action 抛异常、超时抛异常，杜绝静默丢失（联调期最大的敌人）。

### 实验验证手段

- `BridgeTest/`：不启动 WebView2，直接以 `SmartInvoker`/`HostBridge` 为对象跑 84 项断言（16 项 TcpService 业务链路 + 53 项传参/返回类型兼容性探针 + 15 项 HostBridge 请求-响应链路），其中 `UseExecutor`/`UseSender` 注入点支持脱离 WebView2 替身测试；
- `TcpTestServer/`：真实 TCP 回环（自动回显 + 手动群发/随机推送），验证端到端收发闭环；
- 历次实验修正记录：`Uint8Array.map` 字节回收陷阱、Semi Tabs `tabList` 平铺误用、无参方法绕过序列化、COM 封送 PascalCase 失真等，均已沉淀为桥接层代码与注释。

### 方案结论

| 问题 | 结论 |
|---|---|
| 前端直接传对象给 C#？ | ✅ 可行，边界上序列化不可避免（VARIANT 封送），但完全下沉到桥接层，业务两侧零感知 |
| 后端主动调前端并拿结果？ | ✅ `ExecuteScriptWithResultAsync` 一步到位，Promise 自动 await，无需回执协议 |
| UI 开发效率？ | ✅ React + Semi UI 生态远胜 WPF 原生；桌面密度约定（12px 基准）见 `Style.css` |
| 主要代价？ | WebView2 运行时依赖（~180MB）、桥接调用 UI 线程约束、需自建调试链路（DEBUG 开放 DevTools） |

## 许可

内部实验项目。
