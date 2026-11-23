using System.Windows;

namespace MyASR;

public partial class ErrorWindow : Window
{
    public ErrorWindow(string title, string details)
    {
        InitializeComponent();
        Title = title;
        Details.Text = details;
    }
}
