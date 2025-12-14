using CoreGraphics;
using CoreImage;
using Foundation;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UIKit;

namespace TuwaiqAsyncImgFilter;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    async void OnUploadClicked(object sender, EventArgs e)
    {
        var file = await FilePicker.Default.PickAsync();
        if (file == null)
            return;

        var stream = await file.OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        _leftImageBytes = memory.ToArray();
        _rightImageBytes = (byte[])_leftImageBytes.Clone();

        LeftImageView.Source = ImageSource.FromStream(() => new MemoryStream(_leftImageBytes));
        RightImageView.Source = ImageSource.FromStream(() => new MemoryStream(_rightImageBytes));
    }

    async void OnLeftStepClicked(object sender, EventArgs e)
    {
        if (_leftImageBytes == null)
            return;

        LeftLoadingLabel.Opacity = 1;
        var currentBytes = _leftImageBytes;
        var filteredBytes = await Task.Run(() => ApplyRandomPixelFilter(currentBytes));
        _leftImageBytes = filteredBytes;
        LeftImageView.Source = ImageSource.FromStream(() => new MemoryStream(_leftImageBytes));
        LeftLoadingLabel.Opacity = 0;
    }

    void OnRightStartClicked(object sender, EventArgs e)
    {
        if (_rightImageBytes == null)
            return;

        if (_isRightLoopRunning)
        {
            RightStartButton.Text = "Start";
            _rightLoopCts?.Cancel();
            return;
        }

        _isRightLoopRunning = true;
        RightStartButton.Text = "Stop";
        _rightLoopCts = new CancellationTokenSource();
        _ = RunRightLoop(_rightLoopCts.Token);
    }

    async Task RunRightLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var currentBytes = _rightImageBytes;
            if (currentBytes == null)
                break;

            var filteredBytes = await Task.Run(() => ApplyRandomPixelFilter(currentBytes), token);
            if (token.IsCancellationRequested)
                break;

            _rightImageBytes = filteredBytes;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RightImageView.Source = ImageSource.FromStream(() => new MemoryStream(_rightImageBytes));
            });

            try
            {
                await Task.Delay(400, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _isRightLoopRunning = false;
            RightStartButton.Text = "Start";
            _rightLoopCts?.Dispose();
            _rightLoopCts = null;
        });
    }

    async void OnLeftSaveClicked(object sender, EventArgs e)
    {
        if (_leftImageBytes == null)
            return;

        await SaveImageAsync(_leftImageBytes, "left-image.png");
    }

    async void OnRightSaveClicked(object sender, EventArgs e)
    {
        if (_rightImageBytes == null)
            return;

        await SaveImageAsync(_rightImageBytes, "right-image.png");
    }

    async Task SaveImageAsync(byte[] data, string defaultFileName)
    {
        var tempPath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid()}_{defaultFileName}");
        File.WriteAllBytes(tempPath, data);
        var url = NSUrl.FromFilename(tempPath);
        var picker = new UIDocumentPickerViewController(new[] { url }, UIDocumentPickerMode.ExportToService);
        var tcs = new TaskCompletionSource<bool>();
        picker.DidPickDocumentAtUrls += (s, e) => tcs.TrySetResult(true);
        picker.WasCancelled += (s, e) => tcs.TrySetResult(true);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var controller = UIApplication.SharedApplication.ConnectedScenes
                .OfType<UIWindowScene>()
                .SelectMany(scene => scene.Windows)
                .FirstOrDefault(window => window.IsKeyWindow)?
                .RootViewController;
            while (controller?.PresentedViewController != null)
                controller = controller.PresentedViewController;
            controller?.PresentViewController(picker, true, null);
        });

        await tcs.Task;
        File.Delete(tempPath);
    }

    byte[] ApplyRandomPixelFilter(byte[] imageBytes)
    {
        using var data = NSData.FromArray(imageBytes);
        using var uiImage = UIImage.LoadFromData(data)!;
        using var ciImage = new CIImage(uiImage);

        var random = Random.Shared;
        var hueAngle = (float)(random.NextDouble() * Math.PI * 2);
        using var hueFilter = CIFilter.FromName("CIHueAdjust");
        hueFilter?.SetValueForKey(ciImage, InputImageKey);
        hueFilter?.SetValueForKey(new NSNumber(hueAngle), new NSString("inputAngle"));

        var saturation = (float)(0.6 + random.NextDouble() * 0.8);
        var brightness = (float)(random.NextDouble() * 0.4 - 0.2);
        var contrast = (float)(0.9 + random.NextDouble() * 0.6);

        using var colorFilter = CIFilter.FromName("CIColorControls");
        colorFilter?.SetValueForKey(hueFilter?.OutputImage ?? ciImage, InputImageKey);
        colorFilter?.SetValueForKey(new NSNumber(saturation), new NSString("inputSaturation"));
        colorFilter?.SetValueForKey(new NSNumber(brightness), new NSString("inputBrightness"));
        colorFilter?.SetValueForKey(new NSNumber(contrast), new NSString("inputContrast"));

        var outputImage = colorFilter?.OutputImage ?? hueFilter?.OutputImage ?? ciImage;

        var context = new CIContext();
        using var resultImage = context.CreateCGImage(outputImage, outputImage.Extent)!;
        using var finalImage = UIImage.FromImage(resultImage)!;
        using var pngData = finalImage.AsPNG()!;
        using var pngStream = pngData.AsStream();
        using var memory = new MemoryStream();
        pngStream.CopyTo(memory);
        return memory.ToArray();
    }

    byte[]? _leftImageBytes;
    byte[]? _rightImageBytes;
    bool _isRightLoopRunning;
    CancellationTokenSource? _rightLoopCts;
    static readonly NSString InputImageKey = new("inputImage");
}
