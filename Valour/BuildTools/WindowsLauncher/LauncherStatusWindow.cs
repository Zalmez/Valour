using System.Drawing;
using System.Windows.Forms;

namespace Valour.WindowsLauncher;

internal sealed class LauncherStatusWindow : Form
{
    private readonly Label _titleLabel;
    private readonly Label _statusLabel;
    private readonly Label _percentLabel;
    private readonly ProgressBar _progressBar;

    public LauncherStatusWindow()
    {
        Text = "Valour Updater";
        ClientSize = new Size(460, 170);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ShowIcon = true;

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            BackColor = Color.FromArgb(24, 24, 28)
        };

        _titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Text = "Updating Valour",
            Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.White
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = "Starting launcher...",
            Font = new Font("Segoe UI", 9.75F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(220, 220, 220)
        };

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 20,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };

        _percentLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(180, 180, 180)
        };

        panel.Controls.Add(_percentLabel);
        panel.Controls.Add(_progressBar);
        panel.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Color.Transparent });
        panel.Controls.Add(_statusLabel);
        panel.Controls.Add(_titleLabel);
        Controls.Add(panel);
    }

    public void SetStatus(string message, int? percent = null)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(message, percent)));
            return;
        }

        _statusLabel.Text = message;

        if (percent.HasValue)
        {
            var bounded = Math.Clamp(percent.Value, 0, 100);
            if (_progressBar.Style != ProgressBarStyle.Continuous)
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
            }

            _progressBar.Value = bounded;
            _percentLabel.Text = $"{bounded}%";
        }
        else
        {
            if (_progressBar.Style != ProgressBarStyle.Marquee)
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
                _progressBar.MarqueeAnimationSpeed = 30;
            }

            _percentLabel.Text = string.Empty;
        }
    }

    public void SafeClose()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(SafeClose));
            return;
        }

        Close();
    }
}
