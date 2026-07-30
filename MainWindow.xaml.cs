using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LinguaOrb;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly MediaPlayer _player = new();
    private string? _audioUrl;
    private int _imageRequestId;
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, (string Word, string Ipa, string Phonics)> _offline = new()
    {
        ["蝴蝶"] = ("butterfly", "/ˈbʌtəflaɪ/", "but · ter · fly"),
        ["苹果"] = ("apple", "/ˈæpəl/", "ap · ple"),
        ["快乐"] = ("happy", "/ˈhæpi/", "hap · py"),
        ["朋友"] = ("friend", "/frend/", "friend"),
        ["学习"] = ("learn", "/lɜːn/", "learn"),
        ["太阳"] = ("sun", "/sʌn/", "sun"),
        ["月亮"] = ("moon", "/muːn/", "moon")
    };

    public MainWindow()
    {
        InitializeComponent();
        _healthTimer.Tick += async (_, _) => await RefreshApiStatusAsync();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Left = SystemParameters.WorkArea.Right - Width - 14;
        Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
    }

    private void Orb_Click(object sender, RoutedEventArgs e)
    {
        Card.Visibility = Card.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ApiStatusPanel.Visibility = Card.Visibility;
        if (Card.Visibility == Visibility.Visible)
        {
            InputBox.Focus();
            Keyboard.Focus(InputBox);
            _healthTimer.Start();
            _ = RefreshApiStatusAsync();
        }
        else
        {
            _healthTimer.Stop();
        }
    }

    private async Task RefreshApiStatusAsync()
    {
        _healthTimer.Stop();
        try
        {
            await Task.WhenAll(
                ProbeApiAsync(
                    "https://api.mymemory.translated.net/get?q=%E8%8B%B9%E6%9E%9C&langpair=zh-CN|en",
                    TranslateApiTile, TranslatePingText),
                ProbeApiAsync(
                    "https://api.dictionaryapi.dev/api/v2/entries/en/apple",
                    DictionaryApiTile, DictionaryPingText),
                ProbeApiAsync(
                    "https://commons.wikimedia.org/w/api.php?action=query&format=json&meta=siteinfo&siprop=general&origin=*",
                    ImageApiTile, ImagePingText));
        }
        finally
        {
            if (Card.Visibility == Visibility.Visible)
                _healthTimer.Start();
        }
    }

    private static async Task ProbeApiAsync(string url, Border tile, TextBlock label)
    {
        label.Text = "检测中";
        SetTileColor(tile, label, "#F5F2FA", "#DDD7E8", "#8A8299");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            stopwatch.Stop();
            var milliseconds = stopwatch.ElapsedMilliseconds;

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

            label.Text = $"{milliseconds} ms";
            if (milliseconds < 500)
                SetTileColor(tile, label, "#E8F7EF", "#55B985", "#27845A");
            else if (milliseconds < 1500)
                SetTileColor(tile, label, "#FFF6DF", "#E7B94A", "#A87300");
            else
                SetTileColor(tile, label, "#FFF0E8", "#E99163", "#B85B2C");
        }
        catch
        {
            label.Text = "不可用";
            SetTileColor(tile, label, "#FDEBEC", "#DD7A82", "#B53B46");
        }
    }

    private static void SetTileColor(Border tile, TextBlock label, string background, string border, string text)
    {
        tile.Background = (SolidColorBrush)new BrushConverter().ConvertFromString(background)!;
        tile.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(border)!;
        label.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(text)!;
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        var chinese = InputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(chinese))
        {
            StatusText.Text = "请先输入一个中文词语";
            return;
        }

        StatusText.Text = "正在查找最自然的表达…";
        try
        {
            if (_offline.TryGetValue(chinese, out var known))
            {
                var knownAudioUrl = await TryGetAudioAsync(known.Word);
                ShowResult(known.Word, known.Ipa, known.Phonics, "本地精选词条", knownAudioUrl);
                return;
            }

            var word = await TranslateAsync(chinese);
            var (ipa, audioUrl) = await GetPronunciationAsync(word);
            ShowResult(word, ipa ?? "IPA 暂未收录", BuildPhonics(word), "在线翻译与英英词典", audioUrl);
        }
        catch
        {
            StatusText.Text = "网络暂不可用，请试试：蝴蝶、苹果、快乐、朋友";
        }
    }

    private static async Task<string?> TryGetAudioAsync(string word)
    {
        try
        {
            var (_, audioUrl) = await GetPronunciationAsync(word);
            return audioUrl;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> TranslateAsync(string text)
    {
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair=zh-CN|en";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var translated = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("No translation.");
        return Regex.Replace(translated.Trim().ToLowerInvariant(), @"[^\p{L}\s'-]", "");
    }

    private static async Task<(string? Ipa, string? AudioUrl)> GetPronunciationAsync(string word)
    {
        var firstWord = word.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? word;
        var json = await Http.GetStringAsync($"https://api.dictionaryapi.dev/api/v2/entries/en/{Uri.EscapeDataString(firstWord)}");
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement[0];
        string? ipa = entry.TryGetProperty("phonetic", out var phonetic) ? phonetic.GetString() : null;
        string? audio = null;
        foreach (var item in entry.GetProperty("phonetics").EnumerateArray())
        {
            if (string.IsNullOrWhiteSpace(ipa) &&
                item.TryGetProperty("text", out var text) &&
                !string.IsNullOrWhiteSpace(text.GetString()))
                ipa = text.GetString();

            if (item.TryGetProperty("audio", out var audioItem) &&
                !string.IsNullOrWhiteSpace(audioItem.GetString()))
            {
                var candidate = audioItem.GetString()!;
                if (audio is null || candidate.Contains("-uk.", StringComparison.OrdinalIgnoreCase))
                    audio = candidate.StartsWith("//") ? $"https:{candidate}" : candidate;
                if (candidate.Contains("-uk.", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        return (ipa, audio);
    }

    private static string BuildPhonics(string word)
    {
        var parts = Regex.Split(word, @"(?<=[aeiouy])(?=[^aeiouy\s]{1,2}[aeiouy])",
            RegexOptions.IgnoreCase).Where(x => x.Length > 0);
        return string.Join(" · ", parts);
    }

    private void ShowResult(string word, string ipa, string phonics, string source, string? audioUrl)
    {
        WordText.Text = word;
        IpaText.Text = ipa;
        PhonicsText.Text = $"自然拼读：{phonics}";
        _audioUrl = audioUrl;
        SpeakButton.IsEnabled = !string.IsNullOrWhiteSpace(audioUrl);
        SpeakButton.Opacity = SpeakButton.IsEnabled ? 1 : .55;
        StatusText.Text = SpeakButton.IsEnabled ? $"{source} · 可播放英式发音" : $"{source} · 暂无发音音频";
        _ = LoadIllustrationAsync(word);
    }

    private async Task LoadIllustrationAsync(string word)
    {
        var requestId = ++_imageRequestId;
        ImageLoadingBadge.Visibility = Visibility.Visible;

        try
        {
            var query = $"{word} cartoon illustration";
            var apiUrl =
                "https://commons.wikimedia.org/w/api.php?action=query&generator=search" +
                $"&gsrsearch={Uri.EscapeDataString(query)}&gsrnamespace=6&gsrlimit=8" +
                "&prop=imageinfo&iiprop=url&iiurlwidth=640&format=json&origin=*";

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(apiUrl));
            if (!doc.RootElement.TryGetProperty("query", out var queryNode) ||
                !queryNode.TryGetProperty("pages", out var pages))
                throw new InvalidOperationException("No matching illustration.");

            string? imageUrl = null;
            foreach (var page in pages.EnumerateObject())
            {
                if (!page.Value.TryGetProperty("imageinfo", out var info) || info.GetArrayLength() == 0)
                    continue;

                var first = info[0];
                imageUrl = first.TryGetProperty("thumburl", out var thumb)
                    ? thumb.GetString()
                    : first.TryGetProperty("url", out var original) ? original.GetString() : null;
                if (!string.IsNullOrWhiteSpace(imageUrl)) break;
            }

            if (string.IsNullOrWhiteSpace(imageUrl))
                throw new InvalidOperationException("No usable illustration.");

            var bytes = await Http.GetByteArrayAsync(imageUrl);
            await using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            if (requestId == _imageRequestId)
            {
                IllustrationImage.Source = bitmap;
                IllustrationImage.ToolTip = $"{word} · 图片来自 Wikimedia Commons";
            }
        }
        catch
        {
            // 保留默认猫头鹰插图，图片失败不影响翻译结果。
        }
        finally
        {
            if (requestId == _imageRequestId)
                ImageLoadingBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void Speak_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_audioUrl))
        {
            StatusText.Text = "该词条暂未收录发音音频";
            return;
        }

        try
        {
            _player.Stop();
            _player.Open(new Uri(_audioUrl));
            _player.Play();
            StatusText.Text = "正在播放英式发音…";
        }
        catch
        {
            StatusText.Text = "发音加载失败，请检查网络";
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
