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
    private enum TranslationApiKind { GoogleSingle, GoogleArray, MyMemory, Lingva }

    private sealed class TranslationProvider(string name, TranslationApiKind kind, string endpoint)
    {
        public string Name { get; } = name;
        public TranslationApiKind Kind { get; } = kind;
        public string Endpoint { get; } = endpoint;
        public bool Available { get; set; }
        public long LatencyMs { get; set; } = long.MaxValue;
        public Border? Tile { get; set; }
        public TextBlock? NameText { get; set; }
        public TextBlock? LatencyText { get; set; }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly MediaPlayer _player = new();
    private string? _audioUrl;
    private int _imageRequestId;
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly List<TranslationProvider> _providers =
    [
        new("Google 全球", TranslationApiKind.GoogleSingle, "https://translate.googleapis.com/translate_a/single"),
        new("Google Web", TranslationApiKind.GoogleSingle, "https://translate.google.com/translate_a/single"),
        new("Google 轻量", TranslationApiKind.GoogleArray, "https://clients5.google.com/translate_a/t"),
        new("MyMemory", TranslationApiKind.MyMemory, "https://api.mymemory.translated.net/get"),
        new("Lingva 官方", TranslationApiKind.Lingva, "https://lingva.ml"),
        new("Lingva 欧洲", TranslationApiKind.Lingva, "https://translate.plausibility.cloud"),
        new("Lingva 社区", TranslationApiKind.Lingva, "https://translate.projectsegfau.lt"),
        new("Lingva Garuda", TranslationApiKind.Lingva, "https://lingva.garudalinux.org"),
        new("Lingva Lunar", TranslationApiKind.Lingva, "https://lingva.lunar.icu"),
        new("Lingva Jae", TranslationApiKind.Lingva, "https://translate.jae.fi")
    ];
    private int _selectedProviderIndex;
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
        BuildProviderTiles();
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
            await Task.WhenAll(_providers.Select(ProbeProviderAsync));

            if (!_providers[_selectedProviderIndex].Available)
            {
                var fastest = _providers
                    .Select((provider, index) => (provider, index))
                    .Where(item => item.provider.Available)
                    .OrderBy(item => item.provider.LatencyMs)
                    .FirstOrDefault();
                if (fastest.provider is not null)
                    _selectedProviderIndex = fastest.index;
            }
            UpdateAllProviderTiles();
        }
        finally
        {
            if (Card.Visibility == Visibility.Visible)
                _healthTimer.Start();
        }
    }

    private void BuildProviderTiles()
    {
        TranslationNodesPanel.Children.Clear();
        for (var index = 0; index < _providers.Count; index++)
        {
            var provider = _providers[index];
            var nameText = new TextBlock
            {
                Text = provider.Name,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var latencyText = new TextBlock
            {
                Text = "检测中",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var tile = new Border
            {
                Tag = index,
                Height = 44,
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(3, 5, 3, 4),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = new StackPanel { Children = { nameText, latencyText } }
            };
            tile.MouseLeftButtonUp += ProviderTile_Click;
            provider.Tile = tile;
            provider.NameText = nameText;
            provider.LatencyText = latencyText;
            TranslationNodesPanel.Children.Add(tile);
        }
        UpdateAllProviderTiles();
    }

    private async Task ProbeProviderAsync(TranslationProvider provider)
    {
        provider.LatencyText!.Text = "检测中";
        provider.Available = false;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var translated = await TranslateWithProviderAsync(provider, "苹果", timeout.Token);
            stopwatch.Stop();
            provider.Available = !string.IsNullOrWhiteSpace(translated);
            provider.LatencyMs = stopwatch.ElapsedMilliseconds;
        }
        catch
        {
            provider.Available = false;
            provider.LatencyMs = long.MaxValue;
        }
        UpdateProviderTile(provider, _providers[_selectedProviderIndex] == provider);
    }

    private void ProviderTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int index } || !_providers[index].Available) return;
        _selectedProviderIndex = index;
        UpdateAllProviderTiles();
        StatusText.Text = $"已切换到 {_providers[index].Name} · {_providers[index].LatencyMs} ms";
    }

    private void UpdateAllProviderTiles()
    {
        for (var index = 0; index < _providers.Count; index++)
            UpdateProviderTile(_providers[index], index == _selectedProviderIndex);
    }

    private static void UpdateProviderTile(TranslationProvider provider, bool selected)
    {
        var tile = provider.Tile!;
        var label = provider.LatencyText!;
        provider.NameText!.Text = selected ? $"✓ {provider.Name}" : provider.Name;

        if (!provider.Available)
        {
            label.Text = "不可用";
            SetTileColor(tile, label, "#FDEBEC", selected ? "#7C63D9" : "#DD7A82", "#B53B46", selected ? 2 : 1);
        }
        else if (provider.LatencyMs < 500)
        {
            label.Text = $"{provider.LatencyMs} ms";
            SetTileColor(tile, label, "#E8F7EF", selected ? "#7C63D9" : "#55B985", "#27845A", selected ? 2 : 1);
        }
        else if (provider.LatencyMs < 1500)
        {
            label.Text = $"{provider.LatencyMs} ms";
            SetTileColor(tile, label, "#FFF6DF", selected ? "#7C63D9" : "#E7B94A", "#A87300", selected ? 2 : 1);
        }
        else
        {
            label.Text = $"{provider.LatencyMs} ms";
            SetTileColor(tile, label, "#FFF0E8", selected ? "#7C63D9" : "#E99163", "#B85B2C", selected ? 2 : 1);
        }
    }

    private static void SetTileColor(Border tile, TextBlock label, string background, string border, string text, double thickness)
    {
        tile.Background = (SolidColorBrush)new BrushConverter().ConvertFromString(background)!;
        tile.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(border)!;
        tile.BorderThickness = new Thickness(thickness);
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

    private async Task<string> TranslateAsync(string text)
    {
        var preferred = _providers[_selectedProviderIndex];
        var candidates = new[] { preferred }
            .Concat(_providers.Where(provider => provider != preferred && provider.Available).OrderBy(provider => provider.LatencyMs))
            .Concat(_providers.Where(provider => provider != preferred && !provider.Available))
            .Distinct();

        foreach (var provider in candidates)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var stopwatch = Stopwatch.StartNew();
                var translated = await TranslateWithProviderAsync(provider, text, timeout.Token);
                stopwatch.Stop();
                provider.Available = true;
                provider.LatencyMs = stopwatch.ElapsedMilliseconds;
                _selectedProviderIndex = _providers.IndexOf(provider);
                UpdateAllProviderTiles();
                return NormalizeTranslation(translated);
            }
            catch
            {
                provider.Available = false;
                provider.LatencyMs = long.MaxValue;
                UpdateProviderTile(provider, _providers[_selectedProviderIndex] == provider);
            }
        }
        throw new HttpRequestException("All translation providers failed.");
    }

    private static async Task<string> TranslateWithProviderAsync(
        TranslationProvider provider, string text, CancellationToken cancellationToken)
    {
        var encoded = Uri.EscapeDataString(text);
        string url;
        switch (provider.Kind)
        {
            case TranslationApiKind.GoogleSingle:
                url = $"{provider.Endpoint}?client=gtx&sl=zh-CN&tl=en&dt=t&q={encoded}";
                break;
            case TranslationApiKind.GoogleArray:
                url = $"{provider.Endpoint}?client=dict-chrome&sl=zh-CN&tl=en&q={encoded}";
                break;
            case TranslationApiKind.MyMemory:
                url = $"{provider.Endpoint}?q={encoded}&langpair=zh-CN|en";
                break;
            case TranslationApiKind.Lingva:
                url = $"{provider.Endpoint}/api/v1/zh/en/{encoded}";
                break;
            default:
                throw new NotSupportedException();
        }

        var json = await Http.GetStringAsync(url, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return provider.Kind switch
        {
            TranslationApiKind.GoogleSingle => string.Concat(
                root[0].EnumerateArray().Select(segment => segment[0].GetString())),
            TranslationApiKind.GoogleArray => root[0].GetString() ?? throw new InvalidOperationException(),
            TranslationApiKind.MyMemory => root.GetProperty("responseData").GetProperty("translatedText").GetString()
                                           ?? throw new InvalidOperationException(),
            TranslationApiKind.Lingva => root.GetProperty("translation").GetString()
                                         ?? throw new InvalidOperationException(),
            _ => throw new NotSupportedException()
        };
    }

    private static string NormalizeTranslation(string translated)
    {
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
