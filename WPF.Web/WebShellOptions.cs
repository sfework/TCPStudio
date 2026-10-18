using WPF.Web.Common;

namespace WPF.Web
{
    /// <summary>
    /// WebView2 外壳配置。消费方给 <see cref="ShellWindowBase.Options"/> 赋值即可定制，
    /// 无需覆写任何方法；需要更细控制时可覆写 <see cref="ShellWindowBase.OnWebViewLoaded"/> 等虚方法。
    /// </summary>
    public class WebShellOptions
    {
        /// <summary>
        /// 注入到页面脚本的 HostObject 集合。
        /// Key 为 JS 侧访问名（如 "User"），Value 为已包装好的 SmartInvoker。
        /// </summary>
        public Dictionary<string, SmartInvoker> HostObjects { get; } = [];

        /// <summary>
        /// DEBUG 模式下的前端开发服务器地址；为 null 时 Debug 下不加载任何页面。
        /// </summary>
        public Uri? DevServerUri { get; set; }

        /// <summary>
        /// Release 模式下 wwwroot 的虚拟主机名（页面通过 https://{VirtualHostName}/ 访问）。
        /// </summary>
        public string VirtualHostName { get; set; } = "app.local";

        /// <summary>
        /// Release 模式下 wwwroot 目录。为 null 时使用 程序根目录\wwwroot。
        /// </summary>
        public string? WwwRootPath { get; set; }

        /// <summary>
        /// Release 模式下加载的入口页（相对 wwwroot 根）。
        /// </summary>
        public string EntryPage { get; set; } = "index.html";

        /// <summary>
        /// 是否禁用 WebView2 默认外观（白底闪烁），默认禁用右键菜单等安全项见 ShellWindowBase。
        /// </summary>
        public bool DisableDefaultStatusBar { get; set; } = true;

        /// <summary>
        /// 窗口背景色（模板中顶栏/内容区颜色），null 时使用模板默认值。
        /// </summary>
        public string? CaptionBarColor { get; set; }
    }
}
