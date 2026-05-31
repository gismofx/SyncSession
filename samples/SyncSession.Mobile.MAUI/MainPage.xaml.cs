using System;

namespace SyncSystem.Samples.Mobile;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        ClientIdLabel.Text = Guid.NewGuid().ToString();
    }

    private async void OnSyncClicked(object sender, EventArgs e)
    {
        SyncBtn.IsEnabled = false;
        SyncBtn.Text = "Syncing...";

        try
        {
            // TODO: Implement actual sync logic
            await Task.Delay(1000);
            
            LastSyncLabel.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            await DisplayAlert("Success", "Sync completed", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
        finally
        {
            SyncBtn.Text = "Synchronize";
            SyncBtn.IsEnabled = true;
        }
    }
}
