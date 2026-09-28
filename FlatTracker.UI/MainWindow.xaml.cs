using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using FlatTracker.Core.Abstractions;
using FlatTracker.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlatTracker.UI;

public partial class MainWindow : Window
{
    private readonly INotificationPreferencesService _preferences;
    private readonly string[] _districtsFromConfig;
    private readonly UiLogSink _logSink;
    private readonly ScrapeTrigger _trigger;
    private readonly ScrapeRunTracker _runTracker;
    private readonly ILogger<MainWindow> _logger;
    private readonly ObservableCollection<DistrictItem> _districts = new();

    public MainWindow(
        INotificationPreferencesService preferences,
        IOptions<LlmOptions> llmOptions,
        UiLogSink logSink,
        ScrapeTrigger trigger,
        ScrapeRunTracker runTracker,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _preferences = preferences;
        _districtsFromConfig = llmOptions.Value.Districts;
        _logSink = logSink;
        _trigger = trigger;
        _runTracker = runTracker;
        _logger = logger;

        DistrictsList.ItemsSource = _districts;

        _logSink.MessageReceived += OnLogMessage;

        foreach (var message in _logSink.GetBuffer())
        {
            LogBox.AppendText(message + Environment.NewLine);
        }

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLogMessage(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(message + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
    }

    private void RunCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_runTracker.IsRunning)
        {
            _logger.LogInformation("Проверка уже выполняется, ручной запуск пропущен");
            return;
        }

        _logger.LogInformation("Проверка запущена вручную из интерфейса");
        _trigger.Signal();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось построить список районов");
            StatusText.Text = "Ошибка загрузки данных";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _logSink.MessageReceived -= OnLogMessage;
        DistrictsList.ItemsSource = null;
        _districts.Clear();
    }

    private void RefreshAsync()
    {
        var selected = new HashSet<string>(_preferences.SelectedDistricts, StringComparer.OrdinalIgnoreCase);

        _districts.Clear();
        foreach (var district in _districtsFromConfig)
            _districts.Add(new DistrictItem { Name = district, IsSelected = selected.Contains(district) });

        UpdateStatus();
    }

    private async void OnDistrictToggled(object sender, RoutedEventArgs e)
    {
        await SaveSelectionAsync();
    }

    private async void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var district in _districts)
            district.IsSelected = true;
        await SaveSelectionAsync();
    }

    private async void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var district in _districts)
            district.IsSelected = false;
        await SaveSelectionAsync();
    }

    private async Task SaveSelectionAsync()
    {
        var selected = _districts
            .Where(d => d.IsSelected)
            .Select(d => d.Name)
            .ToList();

        await _preferences.SaveAsync(selected, CancellationToken.None);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var selected = _districts.Count(d => d.IsSelected);
        var total = _districts.Count;

        if (total == 0)
        {
            StatusText.Text =
                "Районы пока не найдены. Они появятся после первого сбора объявлений.";
        }
        else if (selected == 0)
        {
            StatusText.Text =
                $"Выбрано районов: 0 из {total}. Уведомления будут приходить по всем районам.";
        }
        else
        {
            StatusText.Text =
                $"Выбрано районов: {selected} из {total}. Уведомления только по выбранным.";
        }
    }
}

public sealed class DistrictItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string Name { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}