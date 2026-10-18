using System.Windows;
using WPF.Web;

namespace TCPStudio
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 进程启动即预热 WebView2 环境，与主窗口构造/首帧渲染并行，缩短界面加载时间
            ShellWindowBase.PreWarmWebView2();
            base.OnStartup(e);
        }
    }
}
