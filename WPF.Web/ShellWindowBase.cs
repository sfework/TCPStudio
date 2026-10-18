using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WPF.Web.Common;

namespace WPF.Web
{
    /// <summary>
    /// WPF + WebView2 外壳窗口基类。
    /// 消费方窗口继承本类即可获得无边框标题栏、窗口按钮与 WebView2 加载能力，
    /// 通过 <see cref="Options"/> 配置宿主对象与页面地址。
    /// 样式模板见 Themes/Generic.xaml（键 ShellWindowTemplate），字体图标见 Assets/iconfont.ttf。
    /// </summary>
    public class ShellWindowBase : Window
    {
        static ShellWindowBase()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(ShellWindowBase),
                new FrameworkPropertyMetadata(typeof(ShellWindowBase)));
        }

        public ShellWindowBase()
        {
            Options = new WebShellOptions();
            Loaded += OnShellLoaded;
        }

        /// <summary>
        /// 后端→前端调用中转服务：业务侧统一用 Bridge.CallAsync（无泛型=单向通知，泛型=请求-响应）；
        /// 经 ExecuteScriptWithResultAsync 调用前端 window.hostRpc.onHostAction 总入口。
        /// </summary>
        public HostBridge Bridge { get; } = new();

        /// <summary>
        /// 模板应用后挂接顶栏按钮事件（Generic.xaml 中无法直接绑定代码事件）。
        /// </summary>
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (GetTemplateChild("min") is Button min) min.Click += Min_Click;
            if (GetTemplateChild("max") is Button max) max.Click += Max_Click;
            if (GetTemplateChild("close") is Button close) close.Click += Close_Click;
        }

        /// <summary>
        /// 外壳配置，构造后即可赋值。
        /// </summary>
        public WebShellOptions Options { get; }

        /// <summary>
        /// 顶栏最小化按钮点击（模板中 min 按钮绑定）。
        /// </summary>
        public void Min_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        /// <summary>
        /// 顶栏最大化/还原按钮点击（模板中 max 按钮绑定）。
        /// </summary>
        public void Max_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        /// <summary>
        /// 顶栏关闭按钮点击（模板中 close 按钮绑定）。
        /// </summary>
        public void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void OnShellLoaded(object sender, RoutedEventArgs e)
        {
            var web = FindWeb();
            if (web == null) return;
            await web.EnsureCoreWebView2Async();
            var core = web.CoreWebView2;
            if (core == null) return;
            ConfigureCoreSettings(core);
            RegisterHostObjects(core);
            Bridge.Attach(core, Dispatcher);
            web.Source = ResolveSource();
            OnWebViewLoaded(core);
        }

        /// <summary>
        /// WebView2 安全与外观默认设置。可覆写以增删配置项。
        /// </summary>
        protected virtual void ConfigureCoreSettings(CoreWebView2 core)
        {
            var s = core.Settings;
            s.IsZoomControlEnabled = false;//禁止缩放
            s.IsPinchZoomEnabled = false;//禁止缩放动作
            s.IsSwipeNavigationEnabled = false;//禁止水平手势
            s.IsStatusBarEnabled = Options.DisableDefaultStatusBar;//关闭状态栏
            s.IsGeneralAutofillEnabled = false;//关闭自动填充
            s.IsPasswordAutosaveEnabled = false;//关闭密码保存
            s.AreBrowserAcceleratorKeysEnabled = false;//关闭快捷键
            s.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.None;//关闭PDF工具栏
            s.AreDefaultContextMenusEnabled = false;//关闭右键菜单
#if DEBUG
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
#endif
        }

        /// <summary>
        /// 将 Options.HostObjects 注册到 CoreWebView2。可覆写以自定义注册逻辑。
        /// </summary>
        protected virtual void RegisterHostObjects(CoreWebView2 core)
        {
            foreach (var kv in Options.HostObjects)
            {
                core.AddHostObjectToScript(kv.Key, kv.Value);
            }
        }

        /// <summary>
        /// 解析当前应加载的页面地址：
        /// DEBUG 使用 Options.DevServerUri；
        /// Release 将 wwwroot 映射为虚拟主机后返回 https://{VirtualHostName}/{EntryPage}。
        /// 可覆写以使用远程地址或自定义映射。
        /// </summary>
        protected virtual Uri? ResolveSource()
        {
#if DEBUG
            return Options.DevServerUri;
#else
            var wwwroot = Options.WwwRootPath
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            var web = FindWeb();
            web?.CoreWebView2?.SetVirtualHostNameToFolderMapping(
                Options.VirtualHostName,
                wwwroot,
                CoreWebView2HostResourceAccessKind.Allow);
            return new Uri($"https://{Options.VirtualHostName}/{Options.EntryPage}");
#endif
        }

        /// <summary>
        /// 页面加载前回调，参数为已配置完成的 CoreWebView2。默认空实现，可覆写。
        /// </summary>
        protected virtual void OnWebViewLoaded(CoreWebView2 core)
        {
        }

        /// <summary>
        /// 在模板中查找名为 "Web" 的 WebView2 控件。
        /// </summary>
        protected WebView2? FindWeb()
        {
            return GetTemplateChild("Web") as WebView2;
        }
    }
}
