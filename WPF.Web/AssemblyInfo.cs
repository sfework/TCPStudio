using System.Windows;

// 让 WPF 在消费方引用本程序集时能自动找到 Themes/Generic.xaml 主题资源。
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            // 主题特定资源字典（无）
    ResourceDictionaryLocation.SourceAssembly   // 通用资源字典位于本程序集
)]
