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

[Activity(Label = "@string/app_name", MainLauncher = true)]
public class MainActivity : Activity
{
    const int RequestCodeCapture = 1000;
    const int RequestCodePermissions = 1001;
    MediaProjectionManager? _projectionManager;
    bool _isScreenSharing = false;

    // Required permissions
    private string[] GetRequiredPermissions()
    {
        var permissions = new List<string>
        {
            Manifest.Permission.SystemAlertWindow,
            Manifest.Permission.WakeLock
        };

        // Only add POST_NOTIFICATIONS for Android 13+ (API 33+)
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            permissions.Add(Manifest.Permission.PostNotifications);
        }

        return permissions.ToArray();
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _projectionManager = (MediaProjectionManager?)GetSystemService(MediaProjectionService);

        // Check and request permissions first
        if (!HasAllPermissions())
        {
            RequestPermissions();
            return;
        }

        InitializeUI();
    }

    private bool HasAllPermissions()
    {
        var requiredPermissions = GetRequiredPermissions();
        foreach (var permission in requiredPermissions)
        {
            if (ContextCompat.CheckSelfPermission(this, permission) != Permission.Granted)
            {
                return false;
            }
        }
        return true;
    }

    private void RequestPermissions()
    {
        var requiredPermissions = GetRequiredPermissions();
        ActivityCompat.RequestPermissions(this, requiredPermissions, RequestCodePermissions);
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

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode == RequestCodePermissions)
        {
            bool allPermissionsGranted = true;
            for (int i = 0; i < grantResults.Length; i++)
            {
                if (grantResults[i] != Permission.Granted)
                {
                    allPermissionsGranted = false;
                    break;
                }
            }

            //if (allPermissionsGranted)
            {
                InitializeUI();
            }
            // else
            // {
            //     // Handle permission denial - show message to user
            //     var txtStatus = FindViewById<TextView>(Resource.Id.txtStatus);
            //     if (txtStatus != null)
            //     {
            //         txtStatus.Text = "Permissions required for screen mirroring";
            //     }
            // }
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode == RequestCodeCapture && resultCode == Result.Ok && data != null)
        {
            Intent serviceIntent = new Intent(this, typeof(ScreenCaptureService));
            serviceIntent.PutExtra("resultCode", (int)resultCode);
            serviceIntent.PutExtra("data", data);

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