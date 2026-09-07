using ButterKnife.Services;
using Microsoft.AspNetCore.Components.Forms;

namespace ButterKnife.Components.Pages;

/// <summary>Image attachments: picking files in the browser, bounded re-encoding, thumbnails, and the expand toggle.</summary>
public partial class Chat
{
    private const int MaxImages = 6;
    private const long MaxImageBytes = 10 * 1024 * 1024;
    private const long ResizeAboveBytes = 1_500_000;
    private const int MaxImageDimension = 1568; // keeps payloads small; matches the Anthropic recommended maximum
    private readonly List<ChatImage> _pendingImages = [];
    private readonly HashSet<ChatImage> _expandedImages = [];
    private string? _imageError;

    private async Task OnFilesSelectedAsync(InputFileChangeEventArgs e)
    {
        _imageError = null;

        foreach (var file in e.GetMultipleFiles(MaxImages))
        {
            if (_pendingImages.Count >= MaxImages)
            {
                _imageError = $"At most {MaxImages} images per message.";
                break;
            }

            if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                _imageError = $"{file.Name} is not an image.";
                continue;
            }

            try
            {
                // Large or unusual formats are re-encoded in the browser as a bounded JPEG; small supported files are kept as-is.
                IBrowserFile source = file;
                var mediaType = file.ContentType;
                if (file.Size > ResizeAboveBytes || !ChatImage.SupportedMediaTypes.Contains(mediaType))
                {
                    source = await file.RequestImageFileAsync("image/jpeg", MaxImageDimension, MaxImageDimension);
                    mediaType = "image/jpeg";
                }

                await using var stream = source.OpenReadStream(MaxImageBytes, _disposed.Token);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, _disposed.Token);
                _pendingImages.Add(new ChatImage(mediaType, buffer.ToArray()));
            }
            catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _imageError = $"{file.Name}: {ex.Message}";
            }
        }
    }

    private void ToggleImage(ChatImage image)
    {
        if (!_expandedImages.Remove(image))
        {
            _expandedImages.Add(image);
        }
    }
}
