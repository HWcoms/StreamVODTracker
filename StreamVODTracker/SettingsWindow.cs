using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace StreamVODTracker;

[SupportedOSPlatform("windows")]
public sealed class SettingsWindow : Window
{
    public SettingsWindow()
    {
        Title = "플랫폼 API 설정";
        Width = 720;
        Height = 720;
        MinWidth = 680;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        var content = new StackPanel { Spacing = 18, Margin = new Thickness(26) };
        content.Children.Add(new TextBlock { Text = "플랫폼 API 설정", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#202230") });
        content.Children.Add(new TextBlock
        {
            Text = "Client ID와 Secret은 Windows 자격 증명 관리자에 이 사용자 전용으로 저장합니다. 치지직 비공식 채널 조회에는 인증정보가 필요하지 않습니다.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush.Parse("#666A78")
        });
        content.Children.Add(BuildPlatformSection("치지직", "CHZZK",
            "https://developers.chzzk.naver.com/",
            "채널 자동 조회는 kimcore/chzzk를 참고한 비공식 API를 사용하며 Client ID와 Secret 없이 채널명과 설명을 가져옵니다. 공식 Client 인증 정보는 별도 보관만 합니다.",
            lookupAvailable: true));
        content.Children.Add(BuildPlatformSection("SOOP", "SOOP",
            "https://developers.sooplive.com/",
            "채널 자동 조회는 공개 비공식 스테이션 API를 사용하며 인증정보 없이 채널명과 프로필 설명을 가져옵니다. Client ID/Secret은 공식 API 연동을 위해 별도 보관합니다.",
            lookupAvailable: false));

        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private Control BuildPlatformSection(string title, string key, string portal, string explanation, bool lookupAvailable)
    {
        var savedCredentials = WindowsCredentialVault.Read(key);
        var clientId = new TextBox { PlaceholderText = "Client ID", MinHeight = 38, Text = savedCredentials?.ClientId ?? "" };
        var secret = new TextBox { PlaceholderText = "Client Secret", PasswordChar = '●', MinHeight = 38 };
        var status = new TextBlock
        {
            Text = savedCredentials is null ? "인증정보가 저장되어 있지 않습니다." : "인증정보가 저장되어 있습니다. Secret은 보안상 다시 표시하지 않습니다.",
            FontSize = 11,
            Foreground = savedCredentials is null ? Brush.Parse("#85899B") : Brush.Parse("#347451"),
            TextWrapping = TextWrapping.Wrap
        };

        var portalButton = new Button { Content = "개발자 포털 열기" };
        portalButton.Click += async (_, _) => await Launcher.LaunchUriAsync(new Uri(portal));
        var saveButton = new Button { Content = "인증정보 저장", MinWidth = 110 };
        saveButton.Click += (_, _) =>
        {
            var id = clientId.Text?.Trim();
            var secretValue = secret.Text;
            if (string.IsNullOrWhiteSpace(id))
            {
                SetStatus(status, "Client ID를 입력해 주세요.", error: true);
                return;
            }
            if (string.IsNullOrEmpty(secretValue) && WindowsCredentialVault.Read(key) is { } existing)
                secretValue = existing.ClientSecret;
            if (string.IsNullOrEmpty(secretValue))
            {
                SetStatus(status, "Client Secret을 입력해 주세요.", error: true);
                return;
            }

            try
            {
                WindowsCredentialVault.Save(key, id, secretValue);
                secret.Text = "";
                SetStatus(status, "인증정보를 Windows 자격 증명 관리자에 저장했습니다.", error: false);
            }
            catch (Exception exception)
            {
                SetStatus(status, $"저장에 실패했습니다: {exception.Message}", error: true);
            }
        };

        var deleteButton = new Button { Content = "저장 정보 삭제", MinWidth = 100 };
        deleteButton.Click += (_, _) =>
        {
            try
            {
                WindowsCredentialVault.Delete(key);
                clientId.Text = "";
                secret.Text = "";
                SetStatus(status, "저장된 인증정보를 삭제했습니다.", error: false);
            }
            catch (Exception exception)
            {
                SetStatus(status, $"삭제에 실패했습니다: {exception.Message}", error: true);
            }
        };

        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#303242") });
        section.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush.Parse("#666A78") });
        section.Children.Add(clientId);
        section.Children.Add(secret);
        section.Children.Add(status);
        section.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { portalButton, saveButton, deleteButton } });
        if (!lookupAvailable)
            section.Children.Add(new TextBlock
            {
                Text = "SOOP 비공식 스테이션 조회에는 인증정보가 필요하지 않습니다. 공식 Client ID/Secret은 자동 조회 요청에 전송하지 않습니다.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = Brush.Parse("#A46A17")
            });

        return new Border
        {
            Background = Brush.Parse("#F7F7FA"),
            BorderBrush = Brush.Parse("#E7E8EE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Child = section
        };
    }

    private static void SetStatus(TextBlock status, string text, bool error)
    {
        status.Text = text;
        status.Foreground = error ? Brushes.IndianRed : Brush.Parse("#347451");
    }
}
