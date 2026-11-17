namespace PrintFins;

sealed class StartupProgress : Form
{
    readonly TextBox _log;
    static StartupProgress? _open;
    static bool _alsoConsole;

    StartupProgress()
    {
        Text = "PrintFins";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 640;
        Height = 360;
        MinimumSize = new Size(420, 240);
        TopMost = true;

        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10f),
            TabStop = false,
        };
        var bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Dock = DockStyle.Bottom,
            Height = 22,
        };
        Controls.Add(_log);
        Controls.Add(bar);
        FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_open, this))
                _open = null;
        };
    }

    public static void Open(bool alsoConsole)
    {
        _alsoConsole = alsoConsole;
        if (alsoConsole)
            return;

        _open = new StartupProgress();
        _open.Show();
        Line("Starting...");
    }

    public static void Line(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        foreach (var raw in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length == 0)
                continue;
            if (_alsoConsole)
            {
                try { Console.WriteLine(raw); } catch { }
            }

            var form = _open;
            if (form is null || form.IsDisposed)
                continue;
            if (form.InvokeRequired)
                form.BeginInvoke(() => form.Append(raw));
            else
                form.Append(raw);
        }

        if (_open is { InvokeRequired: false, IsDisposed: false })
            Application.DoEvents();
    }

    public static void CloseForm()
    {
        var form = _open;
        _open = null;
        if (form is null || form.IsDisposed)
            return;
        form.Close();
        form.Dispose();
        Application.DoEvents();
    }

    void Append(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }
}
