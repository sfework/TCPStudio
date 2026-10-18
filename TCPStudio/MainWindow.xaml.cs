using System;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TCPStudio.Services;
using WPF.Web;
using WPF.Web.Common;

namespace TCPStudio
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// 外壳（无边框标题栏、WebView2 加载与安全配置）由 WPF.Web.ShellWindowBase 提供，
    /// 此处仅通过 Options 注入业务宿主对象与页面地址。
    /// </summary>
    public partial class MainWindow : ShellWindowBase
    {
        private readonly TcpService _tcp = new(new JsonDataStore());

        public MainWindow()
        {
            InitializeComponent();
            // DEBUG 前端开发服务器地址
            Options.DevServerUri = new Uri("http://localhost:3000/");
            // 注入业务服务桥接：JS 侧 window.chrome.webview.hostObjects.Tcp.InvokeCall(...)
            Options.HostObjects.Add("Tcp", new SmartInvoker(_tcp));
            // 推送出口：单向通知走无泛型 CallAsync 重载
            _tcp.Attach(Bridge.CallAsync);
            Closing += (_, _) => _tcp.Shutdown();
        }
    }
}
