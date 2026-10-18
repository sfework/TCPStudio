# WPF.Web

WPF + WebView2 桌面应用外壳模板 NuGet 包。提供无边框窗口样式、WebView2 安全配置与 JS↔C# 桥接（SmartInvoker），通过 `WebShellOptions` 配置定制。

## 包含内容

| 类型 | 说明 |
|---|---|
| `ShellWindowBase` | 外壳窗口基类：无边框 + 顶栏按钮 + WebView2 内容区，样式来自 `Themes/Generic.xaml` |
| `WebShellOptions` | 外壳配置对象：宿主对象、开发服务器地址、wwwroot 虚拟主机映射等 |
| `Common.SmartInvoker` | JS↔C# 反射桥接器，包装业务服务后注入 WebView2 |

依赖：`Microsoft.Web.WebView2`（自动传递）。目标框架：`net10.0-windows7.0`。

## 消费方接入步骤

### 1. 引用包

```bash
dotnet add package WPF.Web
```

### 2. 业务窗口继承 `ShellWindowBase`

`MainWindow.xaml`：

```xml
<shell:ShellWindowBase x:Class="MyApp.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:shell="clr-namespace:WPF.Web;assembly=WPF.Web"
    Title="MyApp" Height="600" Width="1000"
    WindowStartupLocation="CenterScreen"
    Icon="/Assets/icon.ico">
</shell:ShellWindowBase>
```

> 无需再写 `WindowChrome`、`Window.Template`，基类默认样式已包含。

`MainWindow.xaml.cs`：

```csharp
using System.Windows;
using WPF.Web;
using WPF.Web.Common;

namespace MyApp
{
    public partial class MainWindow : ShellWindowBase
    {
        public MainWindow()
        {
            InitializeComponent();

            // 配置外壳
            Options.DevServerUri = new Uri("http://localhost:3000/");   // DEBUG 用
            Options.HostObjects.Add("User", new SmartInvoker(new UserServices()));
        }
    }
}
```

### 3. 业务服务（消费方自行维护，不进包）

```csharp
public class UserServices
{
    public string? GetName() => "A123";
    // JS 侧：window.chrome.webview.hostObjects.User.GetName()
}
```

### 4. App.xaml

```xml
<Application x:Class="MyApp.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    StartupUri="MainWindow.xaml">
    <Application.Resources>
    </Application.Resources>
</Application>
```

> 外壳样式、字体图标全部由包内 `Themes/Generic.xaml` 提供，消费方 `App.xaml` 无需再声明。

## WebShellOptions 配置项

| 属性 | 默认值 | 说明 |
|---|---|---|
| `HostObjects` | 空 | 注入页面的宿主对象字典，Key 为 JS 访问名，Value 为 SmartInvoker |
| `DevServerUri` | null | DEBUG 模式前端开发服务器地址 |
| `VirtualHostName` | `app.local` | Release 模式 wwwroot 虚拟主机名 |
| `WwwRootPath` | 程序根目录\wwwroot | Release 模式 wwwroot 物理目录 |
| `EntryPage` | `index.html` | Release 模式入口页 |
| `DisableDefaultStatusBar` | true | 关闭 WebView2 状态栏 |

## 高级定制

需要更细粒度控制时，覆写 `ShellWindowBase` 的虚方法：

- `ConfigureCoreSettings(CoreWebView2)` — 调整 WebView2 安全/外观设置
- `RegisterHostObjects(CoreWebView2)` — 自定义宿主对象注册
- `ResolveSource()` — 自定义页面加载地址（如远程站点）
- `OnWebViewLoaded(CoreWebView2)` — 页面加载前回调

## 打包发布

```bash
dotnet pack WPF.Web/WPF.Web.csproj -c Release
# 产物：WPF.Web/bin/Release/WPF.Web.1.0.0.nupkg
```

发布到自有源：`dotnet nuget push <nupkg> --source <feed-url> --api-key <key>`
