using Microsoft.Win32;
using System.Windows;
using System.IO;
using System.Diagnostics;

namespace LostArkPatcher
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Title += " v1.0";
            Controllers.InitializeOnMainWindowLoad(this);
            Controllers.SaveSettingsOnMainWindowClose(this);
        }
    }
}