using System.Windows;

namespace Clementine.App.Settings;

/// <summary>Come si usa: al primo avvio e dal menu "Come si usa".</summary>
public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();
        Theme.Apply(this);
    }

    void Start_Click(object sender, RoutedEventArgs e) => Close();
}
