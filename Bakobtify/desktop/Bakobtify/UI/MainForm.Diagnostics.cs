namespace Bakobtify.UI;

internal sealed partial class MainForm
{
    private readonly Queue<(string Area, string Text)> _diagnosticLog = new();
    private TextBox? _diagnosticView;
    private string? _diagnosticFilter;
    private long _switchStartedAt;
    private string? _lastLoggedPhase;

    private void BuildDiagnostics(Surface page)
    {
        Span(page, Theme.Label("Diagnóstico", 32, bold: true), 0, 0, 53);
        Span(page, Theme.Label("Inicialização, recomendações e reprodução. O registro fica apenas nesta sessão.",
            13, Theme.Muted), 0, 57, 36);
        var back = ButtonAt(page, "Configurações", "", () => Navigate(new(Page.Settings)));
        var clear = ButtonAt(page, "Limpar registro", "", () => { _diagnosticLog.Clear(); RefreshDiagnosticLog(); });
        var copy = ButtonAt(page, "Copiar registro", "", () =>
        {
            var lines = FilteredDiagnosticLines();
            if (lines.Length == 0) return;
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            Notify("Registro copiado.");
        });
        back.FillColor = clear.FillColor = copy.FillColor = Theme.Surface;
        Place(back, 0, 105, 134, 37);
        Place(clear, 145, 105, 134, 37);
        Place(copy, 290, 105, 134, 37);
        string?[] categories = [null, "Aplicativo", "Algoritmo", "Reprodução"];
        var filters = new List<(SoftButton Button, string? Area)>();
        foreach (var area in categories)
        {
            var filter = ButtonAt(page, area ?? "Todos", "", () =>
            {
                _diagnosticFilter = area;
                foreach (var (button, category) in filters)
                {
                    button.ForeColor = category == area ? Theme.Accent : Theme.Muted;
                    button.Invalidate();
                }
                RefreshDiagnosticLog();
            });
            filter.FillColor = Theme.Surface;
            filter.ForeColor = area == _diagnosticFilter ? Theme.Accent : Theme.Muted;
            Place(filter, filters.Count * 105, 154, 96, 34);
            filters.Add((filter, area));
        }
        _diagnosticView = new TextBox
        {
            AccessibleName = "Registro de diagnóstico", Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            Font = Theme.Font(Pixels(14)), BackColor = Theme.Surface, ForeColor = Theme.Text
        };
        Span(page, _diagnosticView, 0, 202, 375);
        RefreshDiagnosticLog();
        page.Height = Pixels(597);
    }

    private void LogPlayback(string message)
    {
        if (_switchStartedAt == 0) return;
        LogDiagnostic("Reprodução", message, _switchStartedAt);
    }

    private void LogDiagnostic(string area, string message, long? startedAt = null)
    {
        var elapsed = startedAt is { } started ? $"  +{Environment.TickCount64 - started:N0} ms" : "";
        if (_diagnosticLog.Count >= 200) _diagnosticLog.Dequeue();
        _diagnosticLog.Enqueue((area, $"{DateTime.Now:HH:mm:ss}  [{area}]{elapsed}  {message}"));
        RefreshDiagnosticLog();
    }

    private string[] FilteredDiagnosticLines() => _diagnosticLog
        .Where(entry => _diagnosticFilter is null || entry.Area == _diagnosticFilter)
        .Select(entry => entry.Text).ToArray();

    private void RefreshDiagnosticLog()
    {
        if (_diagnosticView is not { IsDisposed: false } view) return;
        var lines = FilteredDiagnosticLines();
        view.Lines = lines.Length == 0 ? ["Nenhum evento nesta categoria."] : lines;
        view.SelectionStart = view.TextLength;
        view.ScrollToCaret();
    }
}
