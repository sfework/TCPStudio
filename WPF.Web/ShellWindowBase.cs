using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        // WebView2 环境预热任务：进程启动即创建浏览器环境（与 WPF 首帧渲染并行），
        // 窗口 Loaded 时复用，节省 EnsureCoreWebView2Async 的数百毫秒冷启动耗时
        private static Task<CoreWebView2Environment>? _prewarmEnv;

        static ShellWindowBase()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(ShellWindowBase),
                new FrameworkPropertyMetadata(typeof(ShellWindowBase)));
        }

        /// <summary>
        /// 预热 WebView2 环境：在 App.OnStartup 尽早调用，与窗口构造/布局并行，
        /// 首个窗口 OnShellLoaded 时直接复用已建好的环境，显著缩短启动白屏时间。
        /// 重复调用安全（幂等）。
        /// </summary>
        public static void PreWarmWebView2()
        {
            _prewarmEnv ??= CoreWebView2Environment.CreateAsync(null, null, new CoreWebView2EnvironmentOptions
            {
                // 桌面内嵌场景禁用浏览器后台行为：启动期组件更新/网络预检/域服务列表拉取，
                // 减少首启联网等待；occluded 降频会导致最小化后推送延迟，一并关闭
                AdditionalBrowserArguments = "--disable-background-networking --disable-component-update --disable-features=OptimizationHints,MediaRouter --disable-backgrounding-occluded-windows --disable-renderer-backgrounding",
            });
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
            // 复用预热环境；未预热或预热失败则回退默认创建（传 null 即控件自行创建）
            CoreWebView2Environment? env = null;
            if (_prewarmEnv != null)
            {
                try { env = await _prewarmEnv; }
                catch { env = null; }
            }
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            if (core == null) return;
            core.ContextMenuRequested += (sender, args) =>
            {
                // 在 Release 下，彻底吞掉所有右键菜单请求
            #if !DEBUG
                args.Handled = true;
            #endif
            };
            ConfigureCoreSettings(core);
            RegisterHostObjects(core);
            Bridge.Attach(core, Dispatcher);
            _web = web;
            // WebView2 是子 HWND（airspace），加载期以负 Margin 停靠到窗口外（被主窗口裁剪不可见），
            // 让壳侧加载动画可见；首次导航完成后归位并隐藏动画。
            // 不能用 Collapsed/Visibility 隐藏——会阻止 HWND 创建导致内核无法初始化
            core.NavigationCompleted += OnCoreNavigationCompleted;
            web.Source = ResolveSource();
            OnWebViewLoaded(core);
        }

        private WebView2? _web;
        private bool _isFirstNavigation = true;

        /// <summary>首次导航完成：WebView2 归位显示，淡出并移除壳侧加载动画。</summary>
        private void OnCoreNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!_isFirstNavigation) return;
            _isFirstNavigation = false;
            if (_web != null) _web.Margin = new Thickness(4, 0, 4, 4);
            if (GetTemplateChild("loading") is not Border overlay) return;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
            // 注意：不可先 Freeze 再挂 Completed——冻结后 AddEventHandler 会抛 InvalidOperationException
            fade.Completed += (_, _) => overlay.Visibility = Visibility.Collapsed;
            overlay.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>
        /// WebView2 安全与外观默认设置。可覆写以增删配置项。
        /// </summary>
        protected virtual void ConfigureCoreSettings(CoreWebView2 core)
        {
            var s = core.Settings;
            // 缩放
            s.IsZoomControlEnabled = false;
            s.IsPinchZoomEnabled = false;

            // 手势 / 导航
            s.IsSwipeNavigationEnabled = false;

            // 状态栏：Options 属性若是 "Disable" 语义，要取反
            s.IsStatusBarEnabled = false;   // 禁用状态栏（默认 false，避免被前端 JS 误读）

            // 自动填充 & 密码
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;

            // PDF 工具栏：None = 全部显示；要全部隐藏需按位或所有项
            s.HiddenPdfToolbarItems =
                CoreWebView2PdfToolbarItems.Save | CoreWebView2PdfToolbarItems.Print
            | CoreWebView2PdfToolbarItems.SaveAs | CoreWebView2PdfToolbarItems.ZoomIn
            | CoreWebView2PdfToolbarItems.ZoomOut | CoreWebView2PdfToolbarItems.Rotate
            | CoreWebView2PdfToolbarItems.FitPage | CoreWebView2PdfToolbarItems.PageLayout
            | CoreWebView2PdfToolbarItems.Bookmarks | CoreWebView2PdfToolbarItems.PageSelector
            | CoreWebView2PdfToolbarItems.Search;

            // 右键菜单 / DevTools / 快捷键：按构建配置区分
#if DEBUG
            s.AreBrowserAcceleratorKeysEnabled = true;
            s.AreDefaultContextMenusEnabled = true;
            s.AreDevToolsEnabled = true;   // 调试时需要
#else
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.AreDefaultContextMenusEnabled    = false;
            s.AreDevToolsEnabled               = false;
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
