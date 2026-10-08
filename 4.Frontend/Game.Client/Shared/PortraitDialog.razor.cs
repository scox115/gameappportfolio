using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Game.Client.Models;
using Game.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;

namespace Game.Client.Shared;

// The popup for uploading a new portrait. The town shows it while it's open; each opening starts
// with no file picked and no error, as a new instance.
public partial class PortraitDialog
{
    [Inject] private GameState State { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;

    /// <summary>Closed by the player (Escape, the ✕ or the backdrop) or after a successful upload.</summary>
    [Parameter] public EventCallback OnClose { get; set; }

    private bool uploading = false;
    private IBrowserFile? selectedFile;
    private ElementReference portraitCloseButton;
    private string uploadError = string.Empty;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // keyboard and screen-reader users land inside the popup
        if (firstRender) await portraitCloseButton.FocusAsync();
    }

    private async Task PortraitPopupKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") await ClosePortraitPopup();
    }

    private async Task ClosePortraitPopup()
    {
        if (!uploading) await OnClose.InvokeAsync();
    }

    private void HandleFileSelected(InputFileChangeEventArgs e)
    {
        selectedFile = e.File;
        uploadError = string.Empty;
    }

    private async Task ExecuteAvatarUpload()
    {
        if (selectedFile == null) return;
        try
        {
            uploading = true;
            uploadError = string.Empty;
            using var content = new MultipartFormDataContent();
            var fileStream = selectedFile.OpenReadStream(maxAllowedSize: 1024 * 1024 * 4);
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(selectedFile.ContentType);
            content.Add(streamContent, "File", selectedFile.Name);

            var res = await Http.PostAsync("/api/v1/players/me/avatar", content);
            if (res.IsSuccessStatusCode)
            {
                var result = await res.Content.ReadFromJsonAsync<UploadResponse>();
                if (result != null) State.UpdateAvatar(result.avatarUrl);
                selectedFile = null;
                await OnClose.InvokeAsync();
            }
            else uploadError = res.StatusCode switch
            {
                // The server says why: not a PNG or JPG, too big, or turned away by the content check.
                HttpStatusCode.BadRequest or HttpStatusCode.ServiceUnavailable => await ProblemText.ReadAsync(res)
                    ?? "That file couldn't be used. Pick a PNG or JPG under 4 MB.",
                HttpStatusCode.RequestEntityTooLarge => "That file couldn't be used. Pick a PNG or JPG under 4 MB.",
                HttpStatusCode.TooManyRequests => "You've changed your portrait a lot recently. Please try again later.",
                _ => "Upload failed. Please try again."
            };
        }
        catch { uploadError = "Upload failed. Make sure the file is a PNG or JPG under 4 MB and try again."; }
        finally { uploading = false; }
    }
}
