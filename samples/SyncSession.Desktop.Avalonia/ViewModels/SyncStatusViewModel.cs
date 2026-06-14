using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SyncSession.Client.Services;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Samples.Desktop.ViewModels;

/// <summary>
/// Manages sync operations and exposes progress to the UI.
/// Overlay states: Syncing → Success | Error → (Dismiss)
/// </summary>
public partial class SyncStatusViewModel : ObservableObject
{
    private readonly SyncCoordinator _coordinator;
    private readonly ILogger<SyncStatusViewModel> _logger;
    private CancellationTokenSource? _cts;

    /// <summary>Raised after a sync operation completes successfully.</summary>
    public event Action? SyncCompleted;

    // --- Status ---
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private DateTime? _lastSyncedAt;

    // --- Overlay visibility ---
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private bool _isSyncComplete;   // success or error — shows OK button
    [ObservableProperty] private bool _isSyncSuccess;
    [ObservableProperty] private bool _isSyncError;
    [ObservableProperty] private string _syncOutcomeMessage = string.Empty;

    // --- Progress (shown during sync) ---
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string? _currentTable;
    [ObservableProperty] private int _tablesCompleted;
    [ObservableProperty] private int _totalTables;
    [ObservableProperty] private long _recordsProcessed;
    [ObservableProperty] private long _totalRecords;
    [ObservableProperty] private double _tableProgressPercent;
    [ObservableProperty] private double _overallProgressPercent;

    // Formatted labels
    public string TableLabel => TotalTables > 0
        ? $"{TablesCompleted} of {TotalTables} tables: {CurrentTable}"
        : CurrentTable ?? string.Empty;
    public string TableRecordsLabel => TotalRecords > 0
        ? $"{RecordsProcessed:N0} / {TotalRecords:N0} records ({TableProgressPercent:F0}%)"
        : $"{TableProgressPercent:F0}%";
    public string OverallLabel => TotalTables > 0
        ? $"{TablesCompleted} of {TotalTables} tables ({OverallProgressPercent:F0}%)"
        : $"{OverallProgressPercent:F0}%";

    // Overlay is visible when syncing OR showing outcome
    public bool IsOverlayVisible => IsSyncing || IsSyncComplete;

    public SyncStatusViewModel(SyncCoordinator coordinator, ILogger<SyncStatusViewModel> logger)
    {
        _coordinator = coordinator;
        _logger      = logger;
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncAsync()
    {
        _cts = new CancellationTokenSource();

        IsSyncing            = true;
        IsSyncComplete       = false;
        IsSyncSuccess        = false;
        IsSyncError          = false;
        OverallProgressPercent = 0;
        TableProgressPercent   = 0;
        TablesCompleted        = 0;
        TotalTables            = 0;
        RecordsProcessed     = 0;
        TotalRecords         = 0;
        StatusMessage        = "Starting sync…";
        CurrentTable         = null;
        OnPropertyChanged(nameof(IsOverlayVisible));

        var progress = new Progress<SyncProgress>(p =>
        {
            CurrentTable           = p.CurrentTable;
            TablesCompleted        = p.TablesCompleted;
            TotalTables            = p.TotalTables;
            RecordsProcessed       = p.RecordsProcessed;
            TotalRecords           = p.TotalRecords;
            TableProgressPercent   = p.TablePercent;
            OverallProgressPercent = p.OverallPercent;
            StatusMessage          = p.StatusMessage;
            OnPropertyChanged(nameof(TableLabel));
            OnPropertyChanged(nameof(TableRecordsLabel));
            OnPropertyChanged(nameof(OverallLabel));
        });

        try
        {
            // Coordinator: skips cleanly when offline (returns a failed result rather
            // than throwing) and retries transient failures before surfacing them here.
            var result = await _coordinator.SyncAsync(progress, requireNetwork: true, cancellationToken: _cts.Token);

            OverallProgressPercent = 100;
            TableProgressPercent   = 100;

            if (result.Success)
            {
                LastSyncedAt       = DateTime.Now;
                IsOnline           = true;
                IsSyncSuccess      = true;
                SyncOutcomeMessage = "Sync completed successfully.";
                SyncCompleted?.Invoke();
            }
            else
            {
                // Coordinator returns a failed result (no throw) when offline.
                IsOnline           = false;
                IsSyncError        = true;
                SyncOutcomeMessage = $"Sync failed: {result.ErrorMessage}";
            }
        }
        catch (OperationCanceledException)
        {
            IsSyncError        = true;
            SyncOutcomeMessage = "Sync was cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed");
            IsOnline           = false;
            IsSyncError        = true;
            SyncOutcomeMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsSyncing      = false;
            IsSyncComplete = true;
            CurrentTable   = null;
            _cts?.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(IsOverlayVisible));
            SyncCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanSync() => !IsSyncing && !IsSyncComplete;

    [RelayCommand(CanExecute = nameof(IsSyncing))]
    private void CancelSync() => _cts?.Cancel();

    [RelayCommand]
    private void Dismiss()
    {
        IsSyncComplete         = false;
        IsSyncSuccess          = false;
        IsSyncError            = false;
        SyncOutcomeMessage     = string.Empty;
        StatusMessage          = "Ready";
        OverallProgressPercent = 0;
        TableProgressPercent   = 0;
        TablesCompleted        = 0;
        TotalTables            = 0;
        RecordsProcessed       = 0;
        TotalRecords           = 0;
        CurrentTable           = null;
        OnPropertyChanged(nameof(IsOverlayVisible));
        OnPropertyChanged(nameof(TableLabel));
        OnPropertyChanged(nameof(TableRecordsLabel));
        OnPropertyChanged(nameof(OverallLabel));
        SyncCommand.NotifyCanExecuteChanged();
    }
}
