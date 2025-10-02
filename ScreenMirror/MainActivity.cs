using Android;
using Android.Content;
using Android.Content.PM;
using Android.Media.Projection;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using System.Collections.Generic;
using Android.Widget;

namespace ScreenMirror;

[Activity(Label = "@string/app_name")]
public class MainActivity : Activity
{
    const int RequestCodeCapture = 1000;
    MediaProjectionManager? _projectionManager;
    bool _isScreenSharing = false;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _projectionManager = (MediaProjectionManager?)GetSystemService(MediaProjectionService);

        InitializeUI();
    }

    private void InitializeUI()
    {
        var btnStart = FindViewById<Button>(Resource.Id.btnStartCapture);
        var btnStop = FindViewById<Button>(Resource.Id.btnStopCapture);
        var txtStatus = FindViewById<TextView>(Resource.Id.txtStatus);

        UpdateUIState();

        if (btnStart != null)
        {
            btnStart.Click += (s, e) =>
            {
                if (_projectionManager != null && !_isScreenSharing)
                {
                    Intent captureIntent = _projectionManager.CreateScreenCaptureIntent();
                    StartActivityForResult(captureIntent, RequestCodeCapture);
                }
            };
        }

        if (btnStop != null)
        {
            btnStop.Click += (s, e) =>
            {
                StopScreenSharing();
            };
        }
    }

    private void UpdateUIState()
    {
        var btnStart = FindViewById<Button>(Resource.Id.btnStartCapture);
        var btnStop = FindViewById<Button>(Resource.Id.btnStopCapture);
        var txtStatus = FindViewById<TextView>(Resource.Id.txtStatus);

        if (btnStart != null)
        {
            btnStart.Enabled = !_isScreenSharing;
            btnStart.Text = _isScreenSharing ? "Screen Sharing Active" : "Start Screen Sharing";
        }

        if (btnStop != null)
        {
            btnStop.Enabled = _isScreenSharing;
        }

        if (txtStatus != null)
        {
            txtStatus.Text = _isScreenSharing
                ? "Screen sharing is active. Streaming to your device..."
                : "Ready to start screen sharing";
        }
    }

    private void StopScreenSharing()
    {
        try
        {
            Intent serviceIntent = new Intent(this, typeof(ScreenCaptureService));
            StopService(serviceIntent);

            _isScreenSharing = false;
            UpdateUIState();

            var txtStatus = FindViewById<TextView>(Resource.Id.txtStatus);
            if (txtStatus != null)
            {
                txtStatus.Text = "Screen sharing stopped";
            }
        }
        catch (Exception ex)
        {
            var txtStatus = FindViewById<TextView>(Resource.Id.txtStatus);
            if (txtStatus != null)
            {
                txtStatus.Text = $"Error stopping screen sharing: {ex.Message}";
            }
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode == RequestCodeCapture && resultCode == Result.Ok && data != null)
        {
            var ipAddress = FindViewById<EditText>(Resource.Id.editTxtIpAddress)?.Text;
            var port = FindViewById<EditText>(Resource.Id.editTxtPort)?.Text;

            if (string.IsNullOrEmpty(ipAddress) || string.IsNullOrEmpty(port))
            {
                Toast.MakeText(this, "IP address and port cannot be empty", ToastLength.Short)?.Show();
                return;
            }

            Intent serviceIntent = new Intent(this, typeof(ScreenCaptureService));
            serviceIntent.PutExtra("resultCode", (int)resultCode);
            serviceIntent.PutExtra("data", data);
            serviceIntent.PutExtra("ipAddress", ipAddress);
            serviceIntent.PutExtra("port", port);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                StartForegroundService(serviceIntent);
            else
                StartService(serviceIntent);

            // Update state to reflect that screen sharing has started
            _isScreenSharing = true;
            UpdateUIState();
        }
        else if (requestCode == RequestCodeCapture)
        {
            // User denied screen capture permission
            var txtStatus = FindViewById<TextView>(Resource.Id.txtStatus);
            if (txtStatus != null)
            {
                txtStatus.Text = "Screen capture permission denied";
            }
        }
    }
}