using System;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SyncSession.Core.DTOs;
using SyncSession.Samples.Desktop;

namespace SyncSession.Samples.Desktop.ViewModels;

/// <summary>
/// Fetches and displays recent SyncSession summaries from the server.
/// [38l] Switched from SyncActivityLog to SyncSessionSummary (single source of truth).
/// </summary>
public partial class SyncLogViewModel : ObservableObject
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly ILogger<SyncLogViewModel> _logger;

    [ObservableProperty] private ObservableCollection<SyncSessionSummary> _entries = [];
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private int _pageSize = 50;

    public SyncLogViewModel(IHttpClientFactory httpFactory, AppSettings settings, ILogger<SyncLogViewModel> logger)
    {
        _http     = httpFactory.CreateClient();
        _settings = settings;
        _logger   = logger;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading    = true;
        ErrorMessage = null;
        try
        {
            var url    = BuildUrl();
            var result = await _http.GetFromJsonAsync<SyncSessionSummary[]>(url);
            if (result != null)
            {
                Entries = new ObservableCollection<SyncSessionSummary>(result);
            }
            IsOnline = true;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Sync log unavailable — server unreachable");
            IsOnline     = false;
            ErrorMessage = "Server unreachable — sync log unavailable offline.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load sync session log");
            ErrorMessage = ex.Message;
        }
        finally { IsLoading = false; }
    }

    private string BuildUrl()
    {
        var url = $"{_settings.ServerUrl.TrimEnd('/')}/api/v1/sync/sessions?pageSize={PageSize}";
        if (_settings.TenantId.HasValue)
            url += $"&tenantId={_settings.TenantId}";
        return url;
    }
}
