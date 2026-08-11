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
    private enum AssistantMode
    {
        ZhWordToEnglish,
        EnWordToChinese,
        ZhParagraphToEnglish,
        EnParagraphToChinese
    }
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

    private sealed class AudioProvider(string name, string endpoint, bool dictionary = false)
    {
        public string Name { get; } = name;
        public string Endpoint { get; } = endpoint;
        public bool IsDictionary { get; } = dictionary;
        public bool Available { get; set; }
        public long LatencyMs { get; set; } = long.MaxValue;
        public Border? Tile { get; set; }
        public TextBlock? NameText { get; set; }
        public TextBlock? LatencyText { get; set; }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly MediaPlayer _player = new();
    private string? _audioFilePath;
    private string? _currentWord;
    private string? _dictionaryAudioUrl;
    private int _audioRequestId;
    private int _imageRequestId;
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _deerAnimationTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private BitmapImage[] _deerAnimationFrames = [];
    private int _deerAnimationStep;
    private static readonly int[] DeerAnimationSequence = [0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 2, 1];
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
    private readonly List<AudioProvider> _audioProviders =
    [
        new("词典原声", "https://api.dictionaryapi.dev", dictionary: true),
        new("语音 全球", "https://translate.google.com"),
        new("语音 英国", "https://translate.google.co.uk"),
        new("语音 香港", "https://translate.google.com.hk"),
        new("语音 澳洲", "https://translate.google.com.au"),
        new("语音 加拿大", "https://translate.google.ca"),
        new("语音 日本", "https://translate.google.co.jp"),
        new("语音 印度", "https://translate.google.co.in"),
        new("语音 新加坡", "https://translate.google.com.sg"),
        new("语音 新西兰", "https://translate.google.co.nz")
    ];
    private int _selectedAudioProviderIndex;
    private bool _audioStatusInitialized;
    private AssistantMode _mode = AssistantMode.ZhWordToEnglish;
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
        BuildAudioProviderTiles();
        _healthTimer.Tick += async (_, _) => await RefreshApiStatusAsync();
        _deerAnimationFrames =
        [
            LoadResourceBitmap("Assets/deer-drink-1.png"),
            LoadResourceBitmap("Assets/deer-drink-2.png"),
            LoadResourceBitmap("Assets/deer-drink-3.png")
        ];
        _deerAnimationTimer.Tick += DeerAnimationTimer_Tick;
        _deerAnimationTimer.Start();
    }

    private static BitmapImage LoadResourceBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri($"pack://application:,,,/{path}", UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void DeerAnimationTimer_Tick(object? sender, EventArgs e)
    {
        var frame = _deerAnimationFrames[DeerAnimationSequence[_deerAnimationStep]];
        if (OrbButton.Template.FindName("DeerAnimationImage", OrbButton) is Image orbImage)
            orbImage.Source = frame;
        HeaderDeerAnimationImage.Source = frame;
        _deerAnimationStep = (_deerAnimationStep + 1) % DeerAnimationSequence.Length;
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
            if (_audioStatusInitialized)
                await RefreshAudioStatusAsync();
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

    private void BuildAudioProviderTiles()
    {
        AudioNodesPanel.Children.Clear();
        for (var index = 0; index < _audioProviders.Count; index++)
        {
            var provider = _audioProviders[index];
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
            tile.MouseLeftButtonUp += AudioProviderTile_Click;
            provider.Tile = tile;
            provider.NameText = nameText;
            provider.LatencyText = latencyText;
            AudioNodesPanel.Children.Add(tile);
        }
        UpdateAllAudioProviderTiles();
    }

    private void TranslationTab_Click(object sender, RoutedEventArgs e) => ShowProviderTab(showAudio: false);

    private void AudioTab_Click(object sender, RoutedEventArgs e)
    {
        ShowProviderTab(showAudio: true);
        if (!_audioStatusInitialized)
        {
            _audioStatusInitialized = true;
            _ = RefreshAudioStatusAsync();
        }
    }

    private void ShowProviderTab(bool showAudio)
    {
        TranslationNodesPanel.Visibility = showAudio ? Visibility.Collapsed : Visibility.Visible;
        AudioNodesPanel.Visibility = showAudio ? Visibility.Visible : Visibility.Collapsed;
        TranslationTabButton.Background = BrushFrom(showAudio ? "#EEEAF8" : "#7C63D9");
        TranslationTabButton.Foreground = BrushFrom(showAudio ? "#655E74" : "#FFFFFF");
        AudioTabButton.Background = BrushFrom(showAudio ? "#7C63D9" : "#EEEAF8");
        AudioTabButton.Foreground = BrushFrom(showAudio ? "#FFFFFF" : "#655E74");
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

    private async Task ProbeAudioProviderAsync(AudioProvider provider)
    {
        provider.LatencyText!.Text = "检测中";
        provider.Available = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var bytes = await GetAudioBytesAsync(provider, "apple", null, timeout.Token);
            stopwatch.Stop();
            provider.Available = bytes.Length > 256;
            provider.LatencyMs = stopwatch.ElapsedMilliseconds;
        }
        catch
        {
            provider.Available = false;
            provider.LatencyMs = long.MaxValue;
        }
        UpdateAudioProviderTile(provider, _audioProviders[_selectedAudioProviderIndex] == provider);
    }

    private async Task RefreshAudioStatusAsync()
    {
        await Task.WhenAll(_audioProviders.Select(ProbeAudioProviderAsync));
        if (!_audioProviders[_selectedAudioProviderIndex].Available)
        {
            var fastest = _audioProviders
                .Select((provider, index) => (provider, index))
                .Where(item => item.provider.Available)
                .OrderBy(item => item.provider.LatencyMs)
                .FirstOrDefault();
            if (fastest.provider is not null)
                _selectedAudioProviderIndex = fastest.index;
        }
        UpdateAllAudioProviderTiles();
    }

    private void ProviderTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int index } || !_providers[index].Available) return;
        _selectedProviderIndex = index;
        UpdateAllProviderTiles();
        StatusText.Text = $"已切换到 {_providers[index].Name} · {_providers[index].LatencyMs} ms";
    }

    private void AudioProviderTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int index } || !_audioProviders[index].Available) return;
        _selectedAudioProviderIndex = index;
        UpdateAllAudioProviderTiles();
        StatusText.Text = $"已切换到 {_audioProviders[index].Name} · {_audioProviders[index].LatencyMs} ms";
        if (!string.IsNullOrWhiteSpace(_currentWord))
            _ = PrepareAudioAsync(_currentWord, _dictionaryAudioUrl);
    }

    private void UpdateAllProviderTiles()
    {
        for (var index = 0; index < _providers.Count; index++)
            UpdateProviderTile(_providers[index], index == _selectedProviderIndex);
    }

    private void UpdateAllAudioProviderTiles()
    {
        for (var index = 0; index < _audioProviders.Count; index++)
            UpdateAudioProviderTile(_audioProviders[index], index == _selectedAudioProviderIndex);
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

    private static void UpdateAudioProviderTile(AudioProvider provider, bool selected)
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

    private static SolidColorBrush BrushFrom(string color) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;

    private void ZhWordMode_Click(object sender, RoutedEventArgs e) => SetAssistantMode(AssistantMode.ZhWordToEnglish);

    private void EnWordMode_Click(object sender, RoutedEventArgs e) => SetAssistantMode(AssistantMode.EnWordToChinese);

    private void ParagraphMode_Click(object sender, RoutedEventArgs e) => SetAssistantMode(AssistantMode.ZhParagraphToEnglish);

    private void EnParagraphMode_Click(object sender, RoutedEventArgs e) => SetAssistantMode(AssistantMode.EnParagraphToChinese);

    private void SetAssistantMode(AssistantMode mode)
    {
        _mode = mode;
        var paragraph = mode is AssistantMode.ZhParagraphToEnglish or AssistantMode.EnParagraphToChinese;
        WordResultCard.Visibility = paragraph ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Visibility = paragraph ? Visibility.Collapsed : Visibility.Visible;
        IllustrationImage.Visibility = paragraph ? Visibility.Collapsed : Visibility.Visible;
        ParagraphResultCard.Visibility = paragraph ? Visibility.Visible : Visibility.Collapsed;
        SpeakButton.Visibility = mode == AssistantMode.EnWordToChinese || mode == AssistantMode.ZhWordToEnglish
            ? Visibility.Visible
            : Visibility.Collapsed;

        StyleModeButton(ZhWordModeButton, mode == AssistantMode.ZhWordToEnglish);
        StyleModeButton(EnWordModeButton, mode == AssistantMode.EnWordToChinese);
        StyleModeButton(ParagraphModeButton, mode == AssistantMode.ZhParagraphToEnglish);
        StyleModeButton(EnParagraphModeButton, mode == AssistantMode.EnParagraphToChinese);

        switch (mode)
        {
            case AssistantMode.ZhWordToEnglish:
                ModeSubtitle.Text = "中文词语 → 英文单词、音标与发音";
                InputBox.ToolTip = "输入中文，例如：蝴蝶";
                TranslateButton.Content = "翻  译";
                break;
            case AssistantMode.EnWordToChinese:
                ModeSubtitle.Text = "英文词语 → 中文释义";
                InputBox.ToolTip = "输入英文，例如：butterfly";
                TranslateButton.Content = "翻  译";
                break;
            case AssistantMode.ZhParagraphToEnglish:
                ModeSubtitle.Text = "中文段落 → 逐句中英对照与语法结构";
                InputBox.ToolTip = "输入中文段落，可包含多句话";
                TranslateButton.Content = "拆句并翻译";
                break;
            case AssistantMode.EnParagraphToChinese:
                ModeSubtitle.Text = "英文段落 → 逐句英中对照与语法结构";
                InputBox.ToolTip = "输入英文段落，可包含多句话";
                TranslateButton.Content = "拆句并翻译";
                break;
        }
        InputBox.Focus();
    }

    private static void StyleModeButton(Button button, bool selected)
    {
        button.Background = BrushFrom(selected ? "#7C63D9" : "#EEEAF8");
        button.Foreground = BrushFrom(selected ? "#FFFFFF" : "#655E74");
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        var input = InputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            if (_mode is AssistantMode.ZhParagraphToEnglish or AssistantMode.EnParagraphToChinese)
            {
                ParagraphResultsPanel.Children.Clear();
                ParagraphResultsPanel.Children.Add(new TextBlock
                {
                    Text = _mode == AssistantMode.EnParagraphToChinese ? "请先输入英文段落" : "请先输入中文段落",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 18, 0, 0),
                    Foreground = BrushFrom("#8D86A0")
                });
            }
            else
            {
                StatusText.Text = _mode == AssistantMode.EnWordToChinese
                    ? "请先输入一个英文词语"
                    : "请先输入一个中文词语";
            }
            return;
        }

        TranslateButton.IsEnabled = false;
        try
        {
            switch (_mode)
            {
                case AssistantMode.ZhWordToEnglish:
                    await TranslateChineseWordAsync(input);
                    break;
                case AssistantMode.EnWordToChinese:
                    await TranslateEnglishWordAsync(input);
                    break;
                case AssistantMode.ZhParagraphToEnglish:
                    await TranslateParagraphAsync(input);
                    break;
                case AssistantMode.EnParagraphToChinese:
                    await TranslateEnglishParagraphAsync(input);
                    break;
            }
        }
        catch
        {
            if (_mode is AssistantMode.ZhParagraphToEnglish or AssistantMode.EnParagraphToChinese)
                AddParagraphMessage("翻译中断：当前所有翻译节点均不可用");
            else
                StatusText.Text = "当前翻译节点暂不可用，请稍后重试";
        }
        finally
        {
            TranslateButton.IsEnabled = true;
            TranslateButton.Content = _mode is AssistantMode.ZhParagraphToEnglish or AssistantMode.EnParagraphToChinese
                ? "拆句并翻译"
                : "翻  译";
        }
    }

    private async Task TranslateChineseWordAsync(string chinese)
    {
        StatusText.Text = "正在查找最自然的英文表达…";
        if (_offline.TryGetValue(chinese, out var known))
        {
            var knownAudioUrl = await TryGetAudioAsync(known.Word);
            ShowResult(known.Word, known.Ipa, known.Phonics, "本地精选词条", knownAudioUrl);
            return;
        }

        var word = await TranslateAsync(chinese, "zh-CN", "en");
        var (ipa, audioUrl) = await TryGetPronunciationAsync(word);
        ShowResult(word, ipa ?? "IPA 暂未收录", BuildPhonics(word), "中文 → 英文", audioUrl);
    }

    private async Task TranslateEnglishWordAsync(string english)
    {
        StatusText.Text = "正在查找中文释义…";
        var cleanEnglish = Regex.Replace(english.Trim(), @"\s+", " ");
        var chinese = await TranslateAsync(cleanEnglish, "en", "zh-CN");
        var (ipa, audioUrl) = await TryGetPronunciationAsync(cleanEnglish);
        var ipaLine = string.IsNullOrWhiteSpace(ipa) ? $"原词：{cleanEnglish}" : $"{cleanEnglish}  {ipa}";
        ShowResult(chinese, ipaLine, BuildPhonics(cleanEnglish), "英文 → 中文", audioUrl, cleanEnglish);
    }

    private static async Task<(string? Ipa, string? AudioUrl)> TryGetPronunciationAsync(string word)
    {
        try
        {
            return await GetPronunciationAsync(word);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task TranslateParagraphAsync(string paragraph)
    {
        var sentences = CleanChineseSentences(paragraph).Take(30).ToList();
        ParagraphResultsPanel.Children.Clear();
        if (sentences.Count == 0)
        {
            AddParagraphMessage("没有识别到可翻译的中文句子");
            return;
        }

        AddParagraphMessage($"已清洗为 {sentences.Count} 个句子，正在逐句翻译…");
        for (var index = 0; index < sentences.Count; index++)
        {
            TranslateButton.Content = $"翻译中 {index + 1}/{sentences.Count}";
            var english = await TranslateAsync(sentences[index], "zh-CN", "en", preservePunctuation: true);
            if (index == 0) ParagraphResultsPanel.Children.Clear();
            AddSentenceResult(
                index + 1,
                "中文", sentences[index],
                "English", english,
                AnalyzeGrammarStructure(english));
        }
        TranslateButton.Content = "拆句并翻译";
    }

    private async Task TranslateEnglishParagraphAsync(string paragraph)
    {
        var sentences = CleanEnglishSentences(paragraph).Take(30).ToList();
        ParagraphResultsPanel.Children.Clear();
        if (sentences.Count == 0)
        {
            AddParagraphMessage("没有识别到可翻译的英文句子");
            return;
        }

        AddParagraphMessage($"已清洗为 {sentences.Count} 个句子，正在逐句翻译…");
        for (var index = 0; index < sentences.Count; index++)
        {
            TranslateButton.Content = $"翻译中 {index + 1}/{sentences.Count}";
            var chinese = await TranslateAsync(sentences[index], "en", "zh-CN", preservePunctuation: true);
            TranslateButton.Content = $"规范化 {index + 1}/{sentences.Count}";
            var roundTripEnglish = await TranslateAsync(chinese, "zh-CN", "en", preservePunctuation: true);
            var standardEnglish = StandardizeEnglishSentence(roundTripEnglish);
            if (index == 0) ParagraphResultsPanel.Children.Clear();
            AddSentenceResult(
                index + 1,
                "English", sentences[index],
                "中文", chinese,
                AnalyzeGrammarStructure(standardEnglish),
                standardEnglish);
        }
        TranslateButton.Content = "拆句并翻译";
    }

    private static IEnumerable<string> CleanChineseSentences(string paragraph)
    {
        var normalized = paragraph
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        foreach (var raw in Regex.Split(normalized, @"(?<=[。！？!?；;])|\n+"))
        {
            var sentence = Regex.Replace(raw, @"\s+", "").Trim('，', ',', '；', ';');
            if (string.IsNullOrWhiteSpace(sentence)) continue;
            if (!Regex.IsMatch(sentence, @"[。！？!?]$")) sentence += "。";
            yield return sentence;
        }
    }

    private static IEnumerable<string> CleanEnglishSentences(string paragraph)
    {
        var normalized = paragraph
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        foreach (var raw in Regex.Split(normalized, @"(?<=[.!?;])|\n+"))
        {
            var sentence = Regex.Replace(raw, @"\s+", " ").Trim().Trim(',', ';');
            if (string.IsNullOrWhiteSpace(sentence)) continue;
            sentence = char.ToUpperInvariant(sentence[0]) + sentence[1..];
            if (!Regex.IsMatch(sentence, @"[.!?]$")) sentence += ".";
            yield return sentence;
        }
    }

    private static string StandardizeEnglishSentence(string sentence)
    {
        var result = Regex.Replace(sentence.Trim(), @"\s+", " ");
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"\bgonna\b"] = "going to",
            [@"\bwanna\b"] = "want to",
            [@"\bgotta\b"] = "have to",
            [@"\bkinda\b"] = "kind of",
            [@"\bsorta\b"] = "sort of",
            [@"\blemme\b"] = "let me",
            [@"\bgimme\b"] = "give me",
            [@"\bdunno\b"] = "do not know",
            [@"\bcuz\b|\b'cause\b"] = "because",
            [@"\bain't\b"] = "is not",
            [@"\bcan't\b"] = "cannot",
            [@"\bwon't\b"] = "will not",
            [@"\bdon't\b"] = "do not",
            [@"\bdoesn't\b"] = "does not",
            [@"\bdidn't\b"] = "did not",
            [@"\bisn't\b"] = "is not",
            [@"\baren't\b"] = "are not",
            [@"\bwasn't\b"] = "was not",
            [@"\bweren't\b"] = "were not",
            [@"\bi'm\b"] = "I am",
            [@"\byou're\b"] = "you are",
            [@"\bwe're\b"] = "we are",
            [@"\bthey're\b"] = "they are",
            [@"\bi've\b"] = "I have",
            [@"\byou've\b"] = "you have",
            [@"\bi'll\b"] = "I will",
            [@"\byou'll\b"] = "you will"
        };
        foreach (var replacement in replacements)
            result = Regex.Replace(result, replacement.Key, replacement.Value, RegexOptions.IgnoreCase);

        if (result.Length > 0)
            result = char.ToUpperInvariant(result[0]) + result[1..];
        if (!Regex.IsMatch(result, @"[.!?]$")) result += ".";
        return result;
    }

    private void AddParagraphMessage(string message)
    {
        ParagraphResultsPanel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(4, 16, 4, 8),
            Foreground = BrushFrom("#817A94"),
            FontSize = 12
        });
    }

    private void AddSentenceResult(
        int number,
        string sourceLabel,
        string sourceText,
        string targetLabel,
        string targetText,
        string grammar,
        string? standardEnglish = null)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = $"{number}. {sourceLabel}",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFrom("#8A8299")
        });
        content.Children.Add(new TextBlock
        {
            Text = sourceText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 8),
            FontSize = 13,
            Foreground = BrushFrom("#302C48")
        });
        content.Children.Add(new TextBlock
        {
            Text = targetLabel,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFrom("#8A8299")
        });
        content.Children.Add(new TextBlock
        {
            Text = targetText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 8),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFrom("#6B54C6")
        });
        if (!string.IsNullOrWhiteSpace(standardEnglish))
        {
            content.Children.Add(new TextBlock
            {
                Text = "标准英文（回译整理）",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFrom("#8A8299")
            });
            content.Children.Add(new TextBlock
            {
                Text = standardEnglish,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 8),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFrom("#27845A")
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = $"参考语法：{grammar}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = BrushFrom("#A46145")
        });

        ParagraphResultsPanel.Children.Add(new Border
        {
            Background = BrushFrom("#FFFFFF"),
            BorderBrush = BrushFrom("#E6E0F3"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 9),
            Child = content
        });
    }

    private static string AnalyzeGrammarStructure(string sentence)
    {
        var words = Regex.Matches(sentence.ToLowerInvariant(), @"[a-z]+(?:'[a-z]+)?")
            .Select(match => match.Value).ToList();
        if (words.Count == 0) return "未识别到英文句法结构";

        var beVerbs = new HashSet<string> { "am", "is", "are", "was", "were", "be", "been", "being" };
        var auxiliaries = new HashSet<string>
        {
            "can", "could", "will", "would", "shall", "should", "may", "might", "must",
            "do", "does", "did", "have", "has", "had"
        };
        var commonVerbs = new HashSet<string>
        {
            "go", "goes", "went", "come", "comes", "came", "make", "makes", "made", "take", "takes", "took",
            "see", "sees", "saw", "know", "knows", "knew", "think", "thinks", "want", "wants", "need", "needs",
            "like", "likes", "love", "loves", "learn", "learns", "study", "studies", "work", "works", "live", "lives",
            "say", "says", "said", "tell", "tells", "told", "give", "gives", "gave", "use", "uses", "used"
        };
        var verbIndex = words.FindIndex(word =>
            beVerbs.Contains(word) || auxiliaries.Contains(word) || commonVerbs.Contains(word) ||
            word.EndsWith("ed") || word.EndsWith("ing"));
        if (verbIndex <= 0) return "主语(S) + 谓语(V) + 其他成分（建议人工复核）";

        var subject = string.Join(' ', words.Take(verbIndex));
        var verb = words[verbIndex];
        var remainder = string.Join(' ', words.Skip(verbIndex + 1));
        if (beVerbs.Contains(verb))
            return $"主系表 S + V + C｜S: {subject}｜V: {verb}｜C: {remainder}";
        if (auxiliaries.Contains(verb) && verbIndex + 1 < words.Count)
        {
            var predicate = $"{verb} {words[verbIndex + 1]}";
            var rest = string.Join(' ', words.Skip(verbIndex + 2));
            return $"主谓宾 S + V + O｜S: {subject}｜V: {predicate}｜O/补充: {rest}";
        }
        return $"主谓宾 S + V + O｜S: {subject}｜V: {verb}｜O/补充: {remainder}";
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

    private async Task<string> TranslateAsync(
        string text,
        string sourceLang = "zh-CN",
        string targetLang = "en",
        bool preservePunctuation = false)
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
                var translated = await TranslateWithProviderAsync(provider, text, timeout.Token, sourceLang, targetLang);
                stopwatch.Stop();
                provider.Available = true;
                provider.LatencyMs = stopwatch.ElapsedMilliseconds;
                _selectedProviderIndex = _providers.IndexOf(provider);
                UpdateAllProviderTiles();
                return NormalizeTranslation(translated, preservePunctuation);
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
        TranslationProvider provider,
        string text,
        CancellationToken cancellationToken,
        string sourceLang = "zh-CN",
        string targetLang = "en")
    {
        var encoded = Uri.EscapeDataString(text);
        var lingvaSource = sourceLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : sourceLang;
        var lingvaTarget = targetLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : targetLang;
        string url;
        switch (provider.Kind)
        {
            case TranslationApiKind.GoogleSingle:
                url = $"{provider.Endpoint}?client=gtx&sl={Uri.EscapeDataString(sourceLang)}&tl={Uri.EscapeDataString(targetLang)}&dt=t&q={encoded}";
                break;
            case TranslationApiKind.GoogleArray:
                url = $"{provider.Endpoint}?client=dict-chrome&sl={Uri.EscapeDataString(sourceLang)}&tl={Uri.EscapeDataString(targetLang)}&q={encoded}";
                break;
            case TranslationApiKind.MyMemory:
                url = $"{provider.Endpoint}?q={encoded}&langpair={Uri.EscapeDataString(sourceLang)}|{Uri.EscapeDataString(targetLang)}";
                break;
            case TranslationApiKind.Lingva:
                url = $"{provider.Endpoint}/api/v1/{lingvaSource}/{lingvaTarget}/{encoded}";
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

    private static string NormalizeTranslation(string translated, bool preservePunctuation)
    {
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("No translation.");
        if (preservePunctuation) return translated.Trim();
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

    private static async Task<byte[]> GetAudioBytesAsync(
        AudioProvider provider, string word, string? dictionaryAudioUrl, CancellationToken cancellationToken)
    {
        string audioUrl;
        if (provider.IsDictionary)
        {
            audioUrl = dictionaryAudioUrl ?? await FindDictionaryAudioUrlAsync(word, cancellationToken)
                ?? throw new InvalidOperationException("Dictionary audio unavailable.");
        }
        else
        {
            audioUrl = $"{provider.Endpoint}/translate_tts?ie=UTF-8&client=tw-ob&tl=en-GB&q={Uri.EscapeDataString(word)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, audioUrl);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 LinguaOrb/1.0");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length < 256) throw new InvalidOperationException("Audio response was empty.");
        return bytes;
    }

    private static async Task<string?> FindDictionaryAudioUrlAsync(string word, CancellationToken cancellationToken)
    {
        var firstWord = word.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? word;
        var json = await Http.GetStringAsync(
            $"https://api.dictionaryapi.dev/api/v2/entries/en/{Uri.EscapeDataString(firstWord)}",
            cancellationToken);
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement[0].GetProperty("phonetics").EnumerateArray())
        {
            if (!item.TryGetProperty("audio", out var audio) || string.IsNullOrWhiteSpace(audio.GetString())) continue;
            var value = audio.GetString()!;
            return value.StartsWith("//") ? $"https:{value}" : value;
        }
        return null;
    }

    private async Task PrepareAudioAsync(string word, string? dictionaryAudioUrl)
    {
        var requestId = ++_audioRequestId;
        _audioFilePath = null;
        SpeakButton.IsEnabled = false;
        SpeakButton.Opacity = .65;
        SpeakButton.Content = "⏳ 加载发音";

        var preferred = _audioProviders[_selectedAudioProviderIndex];
        var candidates = new[] { preferred }
            .Concat(_audioProviders.Where(provider => provider != preferred && provider.Available)
                .OrderBy(provider => provider.LatencyMs))
            .Concat(_audioProviders.Where(provider => provider != preferred && !provider.Available))
            .Distinct();

        foreach (var provider in candidates)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                var stopwatch = Stopwatch.StartNew();
                var bytes = await GetAudioBytesAsync(provider, word, dictionaryAudioUrl, timeout.Token);
                stopwatch.Stop();
                if (requestId != _audioRequestId) return;

                var cacheDirectory = Path.Combine(Path.GetTempPath(), "LinguaOrb");
                Directory.CreateDirectory(cacheDirectory);
                var cachePath = Path.Combine(cacheDirectory, "current-pronunciation.mp3");
                _player.Close();
                await File.WriteAllBytesAsync(cachePath, bytes);

                provider.Available = true;
                provider.LatencyMs = stopwatch.ElapsedMilliseconds;
                _selectedAudioProviderIndex = _audioProviders.IndexOf(provider);
                _audioFilePath = cachePath;
                SpeakButton.IsEnabled = true;
                SpeakButton.Opacity = 1;
                SpeakButton.Content = "🔊 英式发音";
                UpdateAllAudioProviderTiles();
                StatusText.Text = $"发音已缓存 · {provider.Name} · {provider.LatencyMs} ms";
                return;
            }
            catch
            {
                provider.Available = false;
                provider.LatencyMs = long.MaxValue;
                UpdateAudioProviderTile(provider, _audioProviders[_selectedAudioProviderIndex] == provider);
            }
        }

        if (requestId == _audioRequestId)
        {
            SpeakButton.Content = "🔇 暂无发音";
            StatusText.Text = "所有发音节点暂时不可用";
        }
    }

    private static string BuildPhonics(string word)
    {
        var parts = Regex.Split(word, @"(?<=[aeiouy])(?=[^aeiouy\s]{1,2}[aeiouy])",
            RegexOptions.IgnoreCase).Where(x => x.Length > 0);
        return string.Join(" · ", parts);
    }

    private void ShowResult(
        string word,
        string ipa,
        string phonics,
        string source,
        string? audioUrl,
        string? mediaWord = null)
    {
        var pronunciationWord = mediaWord ?? word;
        WordText.Text = word;
        IpaText.Text = ipa;
        PhonicsText.Text = $"自然拼读：{phonics}";
        _currentWord = pronunciationWord;
        _dictionaryAudioUrl = audioUrl;
        StatusText.Text = $"{source} · 正在预加载发音";
        _ = PrepareAudioAsync(pronunciationWord, audioUrl);
        _ = LoadIllustrationAsync(pronunciationWord);
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
        if (string.IsNullOrWhiteSpace(_audioFilePath) || !File.Exists(_audioFilePath))
        {
            StatusText.Text = "发音尚未加载完成";
            return;
        }

        try
        {
            _player.Stop();
            _player.Open(new Uri(_audioFilePath, UriKind.Absolute));
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
