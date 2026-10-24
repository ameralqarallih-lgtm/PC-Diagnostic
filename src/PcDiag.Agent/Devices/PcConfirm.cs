using System.Drawing;
using System.Windows.Forms;

namespace PcDiag.Agent.Devices;

/// <summary>
/// Asks the person at the PC itself, in a window the recorder shows on its desktop (on top, never from the browser):
/// the dashboard can ask for a change to Windows, but only someone at this PC can let it happen. No answer within the
/// time counts as no; closing the window counts as no.
/// </summary>
internal static class PcConfirm
{
    /// <summary>Shows the question; true only when <paramref name="yes"/> is pressed in time.</summary>
    public static Task<bool> AskAsync(string title, string question, IReadOnlyList<(string Label, string Value)> facts, string yes, TimeSpan timeout)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = Build(title, question, facts, yes, timeout, answer);
                Application.Run(form);
            }
            finally
            {
                answer.TrySetResult(false);
            }
        })
        {
            IsBackground = true,
            Name = "pcdiag-confirm",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return answer.Task;
    }

    private static Form Build(string title, string question, IReadOnlyList<(string Label, string Value)> facts, string yes, TimeSpan timeout, TaskCompletionSource<bool> answer)
    {
        var form = new Form
        {
            Text = "PC Diagnostic: " + title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
            ShowInTaskbar = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
            Font = new Font("Segoe UI", 10f),
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, MaximumSize = new Size(640, 0) };
        layout.Controls.Add(new Label { Text = question, AutoSize = true, MaximumSize = new Size(600, 0), Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) }, 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);
        var row = 1;
        foreach (var (label, value) in facts)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 12, 2) }, 0, row);
            layout.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 2, 0, 2) }, 1, row);
            row++;
        }

        var left = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 12, 0, 0) };
        layout.Controls.Add(left, 0, row);
        layout.SetColumnSpan(left, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = yes, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons, 0, row + 1);
        layout.SetColumnSpan(buttons, 2);
        form.Controls.Add(layout);

        // Cancel is the default: Enter or Esc never says yes by accident.
        form.AcceptButton = cancel;
        form.CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            answer.TrySetResult(true);
            form.Close();
        };
        cancel.Click += (_, _) => form.Close();

        var until = DateTime.UtcNow + timeout;
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) =>
        {
            var remaining = until - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                timer.Stop();
                form.Close();
                return;
            }

            left.Text = $"Closes by itself (no) in {(int)Math.Ceiling(remaining.TotalSeconds)} s.";
        };
        form.Shown += (_, _) =>
        {
            timer.Start();
            form.Activate();
            cancel.Focus();
        };
        form.FormClosed += (_, _) => timer.Dispose();
        return form;
    }
}
