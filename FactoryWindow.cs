using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PixelWorkshop;

internal sealed class FactoryWindow : Window
{
    private readonly WorkerOverlay overlay = new();
    private readonly ManagementWindow management = new();

    internal FactoryWindow()
    {
        Title = "픽셀작업소";
        Width = 104;
        Height = 70;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        var button = new Button
        {
            Content = "▦  작업소",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(40, 66, 90)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(104, 178, 157)),
            BorderThickness = new Thickness(3),
            Margin = new Thickness(0, 16, 0, 0),
            ToolTip = "일꾼 관리"
        };
        Content = button;
        management.PreviewRequested += overlay.Preview;
        button.Click += (_, _) =>
        {
            management.Refresh();
            management.Show();
            management.Activate();
        };
        var menu = new ContextMenu();
        var exit = new MenuItem { Header = "픽셀작업소 종료" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);
        button.ContextMenu = menu;

        Loaded += (_, _) =>
        {
            Place();
            overlay.Owner = this;
            overlay.Show();
            management.Owner = this;
            SystemParameters.StaticPropertyChanged += WorkAreaChanged;
        };
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= WorkAreaChanged;
    }

    private void WorkAreaChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.InvokeAsync(Place);

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + 12;
        Top = area.Bottom - Height;
        overlay.Place(area, Left + Width + 4);
    }
}

internal sealed class ManagementWindow : Window
{
    private readonly ListBox list = new() { MinHeight = 112 };
    private readonly TextBox name = new() { MaxLength = 20 };
    private readonly TextBox parent = new();
    private readonly TextBox create = new() { Text = "09:00", MaxLength = 5 };
    private readonly TextBox clean = new() { Text = "17:30", MaxLength = 5 };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button add = new() { Content = "일꾼 만들기", Margin = new Thickness(0, 12, 0, 0) };

    internal event Action<Worker, bool>? PreviewRequested;

    internal ManagementWindow()
    {
        Title = "픽셀작업소 · 일꾼 관리";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = panel;
        panel.Children.Add(list);
        list.DisplayMemberPath = nameof(WorkerRow.Label);
        var remove = new Button { Content = "선택한 일꾼 해제", Margin = new Thickness(0, 6, 0, 6), IsEnabled = false };
        panel.Children.Add(remove);
        var previews = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var ladder = new Button { Content = "사다리 미리보기", IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
        var rope = new Button { Content = "로프 미리보기", IsEnabled = false };
        previews.Children.Add(ladder);
        previews.Children.Add(rope);
        panel.Children.Add(previews);
        list.SelectionChanged += (_, _) =>
        {
            var selected = list.SelectedItem is WorkerRow;
            remove.IsEnabled = ladder.IsEnabled = rope.IsEnabled = selected;
        };
        ladder.Click += (_, _) => Preview(rope: false);
        rope.Click += (_, _) => Preview(rope: true);
        remove.Click += (_, _) => Try(() =>
        {
            if (list.SelectedItem is not WorkerRow row)
                return;
            Jobs.Remove(row.Worker.Id);
            Refresh();
            feedback.Text = "일꾼과 예약을 해제했어요.";
        });

        panel.Children.Add(new TextBlock { Text = "작업 · 날짜 폴더 만들고 정리하기", FontWeight = FontWeights.Bold });
        Field(panel, "일꾼 이름", name);
        var pathRow = new DockPanel();
        var browse = new Button { Content = "찾기", Padding = new Thickness(8, 0, 8, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(parent);
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "날짜 폴더를 만들 위치" };
            if (dialog.ShowDialog(this) == true)
                parent.Text = dialog.FolderName;
        };
        Field(panel, "대상 폴더", pathRow);
        Field(panel, "매일 생성 시간 (HH:mm)", create);
        Field(panel, "매일 정리 시간 (HH:mm)", clean);
        panel.Children.Add(add);
        panel.Children.Add(feedback);
        add.Click += (_, _) => Try(Add);
        Closing += (_, e) =>
        {
            if (Owner?.IsVisible == true && !Dispatcher.HasShutdownStarted)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    internal void Refresh() => Try(() =>
    {
        var workers = Jobs.Workers();
        list.ItemsSource = workers.Select(worker => new WorkerRow(worker)).ToArray();
        add.IsEnabled = workers.Count < Jobs.MaxWorkers;
        feedback.Text = $"일꾼 {workers.Count}/{Jobs.MaxWorkers}";
    });

    private void Preview(bool rope)
    {
        if (list.SelectedItem is not WorkerRow row)
            return;

        Hide();
        PreviewRequested?.Invoke(row.Worker, rope);
    }

    private void Add()
    {
        if (!Path.IsPathFullyQualified(parent.Text.Trim()) || !Directory.Exists(parent.Text.Trim()))
            throw new InvalidOperationException("존재하는 폴더의 전체 경로를 선택해 주세요.");
        if (!ReadTime(create.Text, out var createAt) || !ReadTime(clean.Text, out var cleanAt) || cleanAt <= createAt)
            throw new InvalidOperationException("시간을 HH:mm으로 입력하고 정리 시간을 생성 시간보다 늦게 설정해 주세요.");

        var label = name.Text.Trim();
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent.Text.Trim()));
        Jobs.Add(new Worker(Guid.NewGuid(), label.Length == 0 ? "일꾼" : label, path, createAt, cleanAt));
        name.Clear();
        Refresh();
        feedback.Text = "일꾼을 만들고 매일 실행할 예약을 등록했어요.";
    }

    private static bool ReadTime(string value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static void Field(Panel panel, string label, FrameworkElement input)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 2) });
        panel.Children.Add(input);
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { feedback.Text = ex.Message; }
    }

    private sealed record WorkerRow(Worker Worker)
    {
        public string Label => $"{Worker.Name} · {Worker.CreateAt:HH\\:mm}–{Worker.CleanAt:HH\\:mm}\n{Worker.Parent}";
    }
}
