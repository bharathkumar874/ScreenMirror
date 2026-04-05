using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Util;
using Android.Views;
using WebSocketSharp;
using Java.Net;
using Java.Util;
using Android.Net.Wifi;
using Android.Net;

namespace ScreenMirror;

[Service(ForegroundServiceType = ForegroundService.TypeMediaProjection, Exported = false, Enabled = true)]
public class ScreenCaptureService : Service
{
    const int NotificationId = 123;
    const string ChannelId = "capture_channel";

    MediaProjection? _mediaProjection;
    VirtualDisplay? _virtualDisplay;
    ImageReader? _imageReader;
    HandlerThread? _handlerThread;
    Handler? _handler;
    WebSocket? _webSocket;

    public override void OnCreate()
    {
        base.OnCreate();
        Log.Info("ScreenCaptureService", "Service OnCreate called");
        CreateNotification();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        int resultCode = intent!.GetIntExtra("resultCode", 0);
        Intent data = (Intent)intent.GetParcelableExtra("data")!;
        string? ipAddress = intent.GetStringExtra("ipAddress");
        string? port = intent.GetStringExtra("port");

        var projectionManager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;

        _mediaProjection = projectionManager.GetMediaProjection(resultCode, data);

        // Register callback before starting capture (required for resource management)
        var callback = new MediaProjectionCallback();
        _mediaProjection.RegisterCallback(callback, _handler);

        _handlerThread = new HandlerThread("ImageReaderThread");
        _handlerThread.Start();
        _handler = new Handler(_handlerThread.Looper!);

        int width = Resources?.DisplayMetrics?.WidthPixels ?? 1080;
        int height = Resources?.DisplayMetrics?.HeightPixels ?? 1920;
        int dpi = (int)(Resources?.DisplayMetrics?.DensityDpi ?? DisplayMetricsDensity.Default);

        // Use compatible image format - MediaProjection typically produces RGBA_8888
        try
        {
            // First try FlexRgb888 for newer devices (API 23+)
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                _imageReader = ImageReader.NewInstance(width, height, ImageFormatType.FlexRgb888, 2);
                Log.Info("ScreenCaptureService", "Using FlexRgb888 format");
            }
            else
            {
                // For older devices, use RGBA_8888 (Android ImageFormat constant = 1)
                _imageReader = ImageReader.NewInstance(width, height, (ImageFormatType)1, 2);
                Log.Info("ScreenCaptureService", "Using RGBA_8888 format for compatibility");
            }
        }
        catch (Exception ex)
        {
            // Ultimate fallback - use RGBA_8888 (Android ImageFormat constant = 1)
            Log.Warn("ScreenCaptureService", $"Preferred format failed, using RGBA_8888 fallback: {ex.Message}");
            _imageReader = ImageReader.NewInstance(width, height, (ImageFormatType)1, 2);
        }
        _virtualDisplay = _mediaProjection.CreateVirtualDisplay(
            "ScreenCapture",
            width, height, dpi,
            DisplayFlags.Presentation,
            _imageReader.Surface, null, _handler
        );

        _imageReader.SetOnImageAvailableListener(new ImageAvailableListener(SendFrame), _handler);

        var wsUrl = "ws://" + ipAddress + ":" + port;
        _webSocket = new WebSocket(wsUrl);
        _webSocket.Connect();
        _webSocket.Send("Hello from ScreenCaptureService");

        return StartCommandResult.Sticky;
    }

    private void CreateNotification()
    {
        Log.Info("ScreenCaptureService", $"Creating notification for Android API {Build.VERSION.SdkInt}");

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var chan = new NotificationChannel(ChannelId, "Screen Capture", NotificationImportance.Low);
            var mgr = (NotificationManager)GetSystemService(NotificationService)!;
            mgr.CreateNotificationChannel(chan);
        }

        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle("Screen Mirroring")
            .SetContentText("Streaming screen to laptop...")
            .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
            .Build();

        // Handle foreground service type properly based on Android version
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake) // Android 14+ (API 34+)
            {
                // Android 14+ requires explicit service type - this is mandatory
                Log.Info("ScreenCaptureService", "Using Android 14+ StartForeground with MediaProjection type");
                StartForeground(NotificationId, notification, ForegroundService.TypeMediaProjection);
            }
            else if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) // Android 10+ (API 29+)
            {
                // Android 10-13: Service type is recommended but not mandatory
                Log.Info("ScreenCaptureService", "Using Android 10-13 StartForeground with MediaProjection type");
                try
                {
                    StartForeground(NotificationId, notification, ForegroundService.TypeMediaProjection);
                }
                catch (Exception ex)
                {
                    // Fallback for Android 10-13 if service type fails
                    Log.Warn("ScreenCaptureService", $"Service type failed, using basic StartForeground: {ex.Message}");
                    StartForeground(NotificationId, notification);
                }
            }
            else
            {
                // For older Android versions, use basic StartForeground
                Log.Info("ScreenCaptureService", "Using basic StartForeground for older Android");
                StartForeground(NotificationId, notification);
            }
            Log.Info("ScreenCaptureService", "StartForeground completed successfully");
        }
        catch (Exception ex)
        {
            // Log the error and use basic StartForeground as ultimate fallback
            Log.Error("ScreenCaptureService", $"StartForeground failed: {ex.GetType().Name} - {ex.Message}");

            // Last resort - try basic StartForeground
            try
            {
                Log.Warn("ScreenCaptureService", "Attempting basic StartForeground as fallback");
                StartForeground(NotificationId, notification);
                Log.Info("ScreenCaptureService", "Fallback StartForeground succeeded");
            }
            catch (Exception fallbackEx)
            {
                Log.Error("ScreenCaptureService", $"Even basic StartForeground failed: {fallbackEx.Message}");
                throw; // Re-throw if even basic StartForeground fails
            }
        }
    }

    private void SendFrame(Image image)
    {
        try
        {
            var planes = image.GetPlanes();
            if (planes == null || planes.Length == 0) return;

            var buffer = planes[0].Buffer;
            if (buffer == null) return;

            int pixelStride = planes[0].PixelStride;
            int rowStride = planes[0].RowStride;
            int rowPadding = rowStride - pixelStride * image.Width;

            Bitmap? bitmap = Bitmap.CreateBitmap(
                image.Width + rowPadding / pixelStride,
                image.Height,
                Bitmap.Config.Argb8888!);
            if (bitmap == null) return;

            bitmap.CopyPixelsFromBuffer(buffer);

            using var ms = new MemoryStream();
            bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 40, ms);
            byte[] jpegData = ms.ToArray();

            _webSocket?.Send(jpegData);
        }
        catch (Exception ex)
        {
            Log.Error("ScreenCaptureService", $"Frame Error: {ex}");
        }
        finally
        {
            image.Close();
        }
    }

    public override void OnDestroy()
    {
        _virtualDisplay?.Release();
        _mediaProjection?.Stop();
        _imageReader?.Close();
        _webSocket?.Close();
        _handlerThread?.QuitSafely();
        base.OnDestroy();
    }
}

class ImageAvailableListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
{
    private readonly Action<Image> _onImage;

    public ImageAvailableListener(Action<Image> onImage)
    {
        _onImage = onImage;
    }

    public void OnImageAvailable(ImageReader? reader)
    {
        using var image = reader?.AcquireLatestImage();
        if (image != null)
        {
            _onImage(image);
        }
    }
}

class MediaProjectionCallback : MediaProjection.Callback
{
    public override void OnStop()
    {
        Log.Info("ScreenCaptureService", "MediaProjection stopped");
        base.OnStop();
    }
}
