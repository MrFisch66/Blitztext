using System.Windows;
using System.Windows.Controls;

namespace Blitztext.Windows;

/// <summary>
/// Small review window shown on a plain left-click of the pill. It displays the last dictated
/// text, lets the user edit it, and copies it to the clipboard — a fallback for when the cursor
/// was in the wrong place while dictating.
/// </summary>
public partial class LastTextWindow : Window
{
    private readonly Func<string, Task> _copyAction;

    public LastTextWindow(Func<string, Task> copyAction)
    {
        _copyAction = copyAction;
        InitializeComponent();
        Title = $"Blitztext {AppInfo.DisplayVersion} – Letzter Text";
    }

    /// <summary>Shows (or re-shows) the window populated with the given text.</summary>
    public void ShowWith(string text)
    {
        TextEditor.Text = text ?? string.Empty;
        StatusLabel.Text = string.IsNullOrWhiteSpace(TextEditor.Text)
            ? "Noch kein Text diktiert."
            : string.Empty;
        UpdateCopyState();

        Show();
        WindowState = WindowState.Normal;
        Activate();
        TextEditor.Focus();
        TextEditor.SelectAll();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Keep the single instance alive so it can be re-shown with fresh text.
        e.Cancel = true;
        Hide();
    }

    private void TextEditor_TextChanged(object sender, TextChangedEventArgs e) => UpdateCopyState();

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = TextEditor.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            await _copyAction(text);
            StatusLabel.Text = "In die Zwischenablage kopiert.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Kopieren fehlgeschlagen: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void UpdateCopyState() => CopyButton.IsEnabled = !string.IsNullOrEmpty(TextEditor.Text);
}
