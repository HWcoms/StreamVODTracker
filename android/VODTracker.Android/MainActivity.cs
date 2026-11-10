using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Content;
using Avalonia.Android;
using Android.Gms.Auth.Api.Identity;
using Android.Gms.Common;
using Android.Gms.Common.Apis;
using Android.Gms.Tasks;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace VODTracker.Android;

[Activity(
    Label = "Stream VOD Tracker",
    Theme = "@style/Theme.AppCompat.Light.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private const int DriveAuthorizationRequestCode = 7314;
    private TaskCompletionSource<AuthorizationResult>? _driveAuthorizationCompletion;
    private IAuthorizationClient? _authorizationClient;
    public static MainActivity? Current { get; private set; }

    public Task<AuthorizationResult> AuthorizeGoogleDriveAsync()
    {
        if (_driveAuthorizationCompletion is not null)
            throw new InvalidOperationException("Google authorization is already in progress.");

        _authorizationClient = Identity.GetAuthorizationClient(this);
        var scopes = new List<Scope>
        {
            new("https://www.googleapis.com/auth/drive.file"),
            new("openid"),
            new("email")
        };
        var request = new AuthorizationRequest.Builder().SetRequestedScopes(scopes).Build();
        var completion = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _driveAuthorizationCompletion = completion;
        _authorizationClient.Authorize(request)
            .AddOnSuccessListener(new DriveAuthorizationSuccessListener(result =>
            {
                if (result.HasResolution)
                {
                    try
                    {
                        StartIntentSenderForResult(result.PendingIntent!.IntentSender,
                            DriveAuthorizationRequestCode, null, 0, 0, 0);
                    }
                    catch (global::Android.Content.IntentSender.SendIntentException exception)
                    {
                        CompleteGoogleAuthorization(exception);
                    }
                }
                else
                {
                    CompleteGoogleAuthorization(result);
                }
            }))
            .AddOnFailureListener(new DriveAuthorizationFailureListener(CompleteGoogleAuthorization));
        return completion.Task;
    }

    public Task RevokeGoogleDriveAuthorizationAsync(string accountEmail)
    {
        var request = RevokeAccessRequest.InvokeBuilder()
            .SetAccount(new global::Android.Accounts.Account(accountEmail, "com.google"))
            .SetScopes(new List<Scope>
            {
                new("https://www.googleapis.com/auth/drive.file"),
                new("openid"),
                new("email")
            })
            .Build();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Identity.GetAuthorizationClient(this).RevokeAccess(request)
            .AddOnSuccessListener(this, new DriveAuthorizationVoidSuccessListener(() => completion.TrySetResult(true)))
            .AddOnFailureListener(this, new DriveAuthorizationFailureListener(exception => completion.TrySetException(exception)));
        return completion.Task;
    }

    private void CompleteGoogleAuthorization(AuthorizationResult result)
    {
        var completion = _driveAuthorizationCompletion;
        _driveAuthorizationCompletion = null;
        if (string.IsNullOrWhiteSpace(result.AccessToken))
            completion?.TrySetException(new InvalidOperationException("Google did not return a Drive access token."));
        else
            completion?.TrySetResult(result);
    }

    private void CompleteGoogleAuthorization(Exception exception)
    {
        var completion = _driveAuthorizationCompletion;
        _driveAuthorizationCompletion = null;
        completion?.TrySetException(exception);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;

        // Keep the Avalonia content inside the system window area so the
        // bottom navigation controls do not cover the last row of the UI.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            Window?.SetDecorFitsSystemWindows(true);
    }

    protected override void OnDestroy()
    {
        _driveAuthorizationCompletion?.TrySetCanceled();
        _driveAuthorizationCompletion = null;
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode == DriveAuthorizationRequestCode)
        {
            if (data is not null)
            {
                try
                {
                    var result = _authorizationClient?.GetAuthorizationResultFromIntent(data)
                        ?? throw new InvalidOperationException("Google authorization did not return a result.");
                    CompleteGoogleAuthorization(result);
                }
                catch (Exception exception)
                {
                    // Google can include a useful API status in the returned intent
                    // even when the activity result itself is RESULT_CANCELED.
                    CompleteGoogleAuthorization(exception);
                }
            }
            else if (resultCode != Result.Ok)
                CompleteGoogleAuthorization(new System.OperationCanceledException("Google account authorization was canceled. Choose an account and grant Drive access to continue."));
            else
                CompleteGoogleAuthorization(new InvalidOperationException("Google authorization returned no result data."));
            return;
        }
        base.OnActivityResult(requestCode, resultCode, data);
    }

    public override void OnBackPressed()
    {
        if (AndroidApp.MainView?.HandleSystemBack() == true) return;
        base.OnBackPressed();
    }
}

internal sealed class DriveAuthorizationSuccessListener(Action<AuthorizationResult> onSuccess)
    : Java.Lang.Object, IOnSuccessListener
{
    public void OnSuccess(Java.Lang.Object? result)
    {
        if (result is AuthorizationResult authorizationResult) onSuccess(authorizationResult);
        else throw new InvalidOperationException("Google authorization returned an unexpected result.");
    }
}

internal sealed class DriveAuthorizationFailureListener(Action<Exception> onFailure)
    : Java.Lang.Object, IOnFailureListener
{
    public void OnFailure(Java.Lang.Exception exception) => onFailure(new InvalidOperationException(exception.Message));
}

internal sealed class DriveAuthorizationVoidSuccessListener(Action onSuccess)
    : Java.Lang.Object, IOnSuccessListener
{
    public void OnSuccess(Java.Lang.Object? result) => onSuccess();
}
