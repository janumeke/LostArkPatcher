using System.Windows;

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
            Closed += ((MainViewModel)DataContext).OnWindowClosed;
        }
    }
}