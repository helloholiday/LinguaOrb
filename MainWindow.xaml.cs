using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace LinguaOrb;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
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

    public MainWindow() => InitializeComponent();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Left = SystemParameters.WorkArea.Right - Width - 14;
        Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
    }

    private void Orb_Click(object sender, RoutedEventArgs e)
    {
        Card.Visibility = Card.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (Card.Visibility == Visibility.Visible)
        {
            InputBox.Focus();
            Keyboard.Focus(InputBox);
        }
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
                ShowResult(known.Word, known.Ipa, known.Phonics, "本地精选词条");
                return;
            }

            var word = await TranslateAsync(chinese);
            var ipa = await GetIpaAsync(word);
            ShowResult(word, ipa ?? "IPA 暂未收录", BuildPhonics(word), "在线翻译与英英词典");
        }
        catch
        {
            StatusText.Text = "网络暂不可用，请试试：蝴蝶、苹果、快乐、朋友";
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

    private static async Task<string?> GetIpaAsync(string word)
    {
        var firstWord = word.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? word;
        var json = await Http.GetStringAsync($"https://api.dictionaryapi.dev/api/v2/entries/en/{Uri.EscapeDataString(firstWord)}");
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement[0];
        if (entry.TryGetProperty("phonetic", out var phonetic) && !string.IsNullOrWhiteSpace(phonetic.GetString()))
            return phonetic.GetString();
        foreach (var item in entry.GetProperty("phonetics").EnumerateArray())
            if (item.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                return text.GetString();
        return null;
    }

    private static string BuildPhonics(string word)
    {
        var parts = Regex.Split(word, @"(?<=[aeiouy])(?=[^aeiouy\s]{1,2}[aeiouy])",
            RegexOptions.IgnoreCase).Where(x => x.Length > 0);
        return string.Join(" · ", parts);
    }

    private void ShowResult(string word, string ipa, string phonics, string source)
    {
        WordText.Text = word;
        IpaText.Text = ipa;
        PhonicsText.Text = $"自然拼读：{phonics}";
        StatusText.Text = source;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
