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
using System.Threading.Tasks;

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

        // Initialize handler thread FIRST before registering callback
        _handlerThread = new HandlerThread("ImageReaderThread");
        _handlerThread.Start();
        _handler = new Handler(_handlerThread.Looper!);

        // Now register callback with properly initialized handler
        var callback = new MediaProjectionCallback();
        _mediaProjection.RegisterCallback(callback, _handler);

        int width = Resources?.DisplayMetrics?.WidthPixels ?? 1080;
        int height = Resources?.DisplayMetrics?.HeightPixels ?? 1920;
        int dpi = (int)(Resources?.DisplayMetrics?.DensityDpi ?? DisplayMetricsDensity.Default);

        // Use compatible image format - Start with most compatible RGBA_8888
        try
        {
            // Use RGBA_8888 (ImageFormat constant = 1) which is most universally supported
            _imageReader = ImageReader.NewInstance(width, height, (ImageFormatType)1, 2);
            Log.Info("ScreenCaptureService", "Using RGBA_8888 format for maximum compatibility");
        }
        catch (Exception ex)
        {
            // If even RGBA_8888 fails, try RGB_565 as last resort
            Log.Warn("ScreenCaptureService", $"RGBA_8888 failed, trying RGB_565: {ex.Message}");
            try
            {
                _imageReader = ImageReader.NewInstance(width, height, (ImageFormatType)4, 2); // RGB_565
                Log.Info("ScreenCaptureService", "Using RGB_565 format as fallback");
            }
            catch (Exception ex2)
            {
                Log.Error("ScreenCaptureService", $"All image formats failed: {ex2.Message}");
                throw;
            }
        }
        _virtualDisplay = _mediaProjection.CreateVirtualDisplay(
            "ScreenCapture",
            width, height, dpi,
            DisplayFlags.Presentation,
            _imageReader.Surface, null, _handler
        );

        _imageReader.SetOnImageAvailableListener(new ImageAvailableListener(SendFrame), _handler);

        // Initialize WebSocket connection with better error handling
        InitializeWebSocketConnection();

        return StartCommandResult.Sticky;
    }

    private void InitializeWebSocketConnection()
    {
        var wsUrl = "wss://screen-mirror-web.onrender.com";
        Log.Info("ScreenCaptureService", $"Connecting to WebSocket: {wsUrl}");

        try
        {
            _webSocket = new WebSocket(wsUrl);

            // Set connection timeout
            _webSocket.WaitTime = TimeSpan.FromSeconds(10);

            // Configure SSL settings for testing (bypass certificate validation)
            _webSocket.SslConfiguration.ServerCertificateValidationCallback =
                (sender, certificate, chain, sslPolicyErrors) =>
                {
                    Log.Info("ScreenCaptureService", $"SSL Certificate validation: {sslPolicyErrors}");
                    return true; // Accept all certificates for testing
                };

            // Set additional connection parameters
            _webSocket.Origin = "https://screen-mirror-web.onrender.com";
            _webSocket.EmitOnPing = true;

            _webSocket.OnOpen += (sender, e) =>
            {
                Log.Info("ScreenCaptureService", "✅ WebSocket connection opened successfully");
                _webSocket.Send("Hello from ScreenCaptureService - Connection established");
            };

            _webSocket.OnError += (sender, e) =>
            {
                Log.Error("ScreenCaptureService", $"❌ WebSocket error: {e.Message}");
                Log.Error("ScreenCaptureService", $"Error details: Exception={e.Exception?.GetType().Name}, InnerException={e.Exception?.InnerException?.Message}");
            };

            _webSocket.OnClose += (sender, e) =>
            {
                Log.Warn("ScreenCaptureService", $"🔌 WebSocket closed: Code={e.Code}, Reason='{e.Reason}', WasClean={e.WasClean}");

                // Attempt to reconnect if connection was not closed cleanly
                if (!e.WasClean && e.Code != 1000)
                {
                    Log.Info("ScreenCaptureService", "Attempting to reconnect WebSocket...");
                    Task.Delay(3000).ContinueWith(_ =>
                    {
                        try
                        {
                            if (_webSocket?.ReadyState == WebSocketState.Closed)
                            {
                                _webSocket.Connect();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error("ScreenCaptureService", $"Reconnection failed: {ex.Message}");
                        }
                    });
                }
            };

            // Attempt connection with error handling
            Log.Info("ScreenCaptureService", "Attempting WebSocket connection...");
            _webSocket.Connect();

            // Give it a moment to establish connection
            Task.Delay(2000).ContinueWith(_ =>
            {
                if (_webSocket?.ReadyState != WebSocketState.Open)
                {
                    Log.Warn("ScreenCaptureService", $"WebSocket not opened after 2s, state: {_webSocket?.ReadyState}");
                    // Try fallback connection method
                    TryFallbackConnection();
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("ScreenCaptureService", $"Failed to initialize WebSocket: {ex.Message}");
            Log.Error("ScreenCaptureService", $"Stack trace: {ex.StackTrace}");
            TryFallbackConnection();
        }
    }

    private void TryFallbackConnection()
    {
        Log.Info("ScreenCaptureService", "Trying fallback connection methods...");

        try
        {
            // Try with different URL or protocol
            var fallbackUrl = "wss://screen-mirror-web.onrender.com"; // Try non-secure first
            Log.Info("ScreenCaptureService", $"Trying fallback URL: {fallbackUrl}");

            _webSocket = new WebSocket(fallbackUrl);
            _webSocket.WaitTime = TimeSpan.FromSeconds(5);

            _webSocket.OnOpen += (sender, e) =>
            {
                Log.Info("ScreenCaptureService", "✅ Fallback WebSocket connection opened successfully");
            };

            _webSocket.OnError += (sender, e) =>
            {
                Log.Error("ScreenCaptureService", $"❌ Fallback WebSocket error: {e.Message}");
            };

            _webSocket.OnClose += (sender, e) =>
            {
                Log.Warn("ScreenCaptureService", $"🔌 Fallback WebSocket closed: Code={e.Code}, Reason='{e.Reason}'");
            };

            _webSocket.Connect();
        }
        catch (Exception ex)
        {
            Log.Error("ScreenCaptureService", $"Fallback connection also failed: {ex.Message}");
        }
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
            bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 30, ms);
            byte[] jpegData = ms.ToArray();

            if (_webSocket?.ReadyState == WebSocketState.Open)
            {
                _webSocket.Send(jpegData);
                Log.Debug("ScreenCaptureService", $"Sent frame to remote server: {jpegData.Length} bytes, {image.Width}x{image.Height}");
            }
            else
            {
                Log.Warn("ScreenCaptureService", $"WebSocket not ready for remote server: {_webSocket?.ReadyState}");
            }
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
