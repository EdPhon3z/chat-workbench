using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace GPTBackup;

public partial class MainWindow : Window
{
    private readonly BackupDatabase _database = new();
    private readonly List<ChatSummary> _scannedChats = [];
    private SearchResult? _selectedResult;
    private bool _pauseRequested;
    private string _lastExtractionMethod = "unknown";
    private GridLength _expandedSideWidth = new(320);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        BackupPanel.Visibility = Visibility.Visible;
        ArchivePanel.Visibility = Visibility.Collapsed;
        ShowBackupPanelButton.Style = (Style)FindResource("TabButton");
        ShowArchivePanelButton.Style = (Style)FindResource("InactiveTabButton");
        RefreshResults();
        RefreshStats();
        await InitializeBrowserAsync();
    }

    private async Task InitializeBrowserAsync()
    {
        SetBusy(true, "Starting browser profile...");

        var profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GPTBackup",
            "WebViewProfile");

        Directory.CreateDirectory(profilePath);
        var environment = await CoreWebView2Environment.CreateAsync(null, profilePath);
        await Browser.EnsureCoreWebView2Async(environment);

        Browser.CoreWebView2.Settings.AreDevToolsEnabled = true;
        Browser.CoreWebView2.Navigate("https://chatgpt.com/");
        SetBusy(false, "Sign in with SSO/MFA if needed, then use the backup controls.");
    }

    private void OpenChatGptButton_Click(object sender, RoutedEventArgs e)
    {
        Browser.CoreWebView2?.Navigate("https://chatgpt.com/");
    }

    private async void BackupCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        await BackupCurrentChatAsync();
    }

    private async void ScanListButton_Click(object sender, RoutedEventArgs e)
    {
        await ScanVisibleChatListAsync();
    }

    private async void BackupAllButton_Click(object sender, RoutedEventArgs e)
    {
        await BackupPendingChatsAsync(GetBackupBatchLimit());
    }

    private void BackupAllCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (BatchSizeBox is not null)
        {
            BatchSizeBox.IsEnabled = BackupAllCheckBox.IsChecked != true;
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _pauseRequested = true;
        StatusText.Text = "Pause requested. Finishing the current chat first...";
    }

    private async void RetryFailedButton_Click(object sender, RoutedEventArgs e)
    {
        _database.ResetFailedBackups();
        RefreshStats();
        RefreshResults();
        await BackupPendingChatsAsync(GetBackupBatchLimit());
    }

    private void CollapseSidePanelButton_Click(object sender, RoutedEventArgs e)
    {
        SetSidePanelCollapsed(true);
    }

    private void ExpandSidePanelButton_Click(object sender, RoutedEventArgs e)
    {
        SetSidePanelCollapsed(false);
    }

    private void ShowBackupPanelButton_Click(object sender, RoutedEventArgs e)
    {
        SetSidePanelCollapsed(false);
        BackupPanel.Visibility = Visibility.Visible;
        ArchivePanel.Visibility = Visibility.Collapsed;
        ShowBackupPanelButton.Style = (Style)FindResource("TabButton");
        ShowArchivePanelButton.Style = (Style)FindResource("InactiveTabButton");
    }

    private void ShowArchivePanelButton_Click(object sender, RoutedEventArgs e)
    {
        SetSidePanelCollapsed(false);
        BackupPanel.Visibility = Visibility.Collapsed;
        ArchivePanel.Visibility = Visibility.Visible;
        ShowBackupPanelButton.Style = (Style)FindResource("InactiveTabButton");
        ShowArchivePanelButton.Style = (Style)FindResource("TabButton");
        SearchBox.Focus();
    }

    private void SetSidePanelCollapsed(bool isCollapsed)
    {
        if (isCollapsed)
        {
            if (LeftColumn.ActualWidth > 80)
            {
                _expandedSideWidth = new GridLength(LeftColumn.ActualWidth);
            }

            ExpandedPanel.Visibility = Visibility.Collapsed;
            CollapsedRail.Visibility = Visibility.Visible;
            MainSplitter.Visibility = Visibility.Collapsed;
            LeftColumn.Width = new GridLength(44);
            return;
        }

        CollapsedRail.Visibility = Visibility.Collapsed;
        ExpandedPanel.Visibility = Visibility.Visible;
        MainSplitter.Visibility = Visibility.Visible;
        LeftColumn.Width = _expandedSideWidth.Value > 80 ? _expandedSideWidth : new GridLength(320);
    }

    private async Task ScanVisibleChatListAsync()
    {
        SetBusy(true, "Discovering chats by scrolling the sidebar...");
        var seen = _database.GetBackupQueue(includeSaved: true)
            .ToDictionary(
                chat => chat.Id,
                chat => new ChatSummary(chat.Id, chat.Title, chat.Url));
        var idleRounds = 0;
        var previousCount = 0;

        for (var round = 1; round <= 600 && idleRounds < 30; round++)
        {
            var json = await Browser.CoreWebView2.ExecuteScriptAsync(ChatListScript);
            var chats = DeserializeScriptResult<List<ChatSummary>>(json) ?? [];

            foreach (var chat in chats)
            {
                seen[chat.Id] = chat;
            }

            if (seen.Count == previousCount)
            {
                idleRounds++;
            }
            else
            {
                idleRounds = 0;
                previousCount = seen.Count;
                _database.UpsertDiscoveredChats(seen.Values);
                RefreshStats();
            }

            SetBusy(true, $"Discovered {seen.Count} chats. Loading more history... ({idleRounds}/30 idle)");

            var movedJson = await Browser.CoreWebView2.ExecuteScriptAsync(ChatSidebarScrollScript);
            var moved = DeserializeScriptResult<bool>(movedJson);
            if (!moved && idleRounds >= 30)
            {
                break;
            }

            await Task.Delay(moved ? 450 : 900);
        }

        _scannedChats.AddRange(seen.Values.OrderBy(chat => chat.Title));
        _database.UpsertDiscoveredChats(_scannedChats);
        RefreshStats();
        RefreshResults();
        SetBusy(false, $"Discovery finished with {_scannedChats.Count} chats.");
    }

    private async Task BackupPendingChatsAsync(int? batchLimit)
    {
        var queue = _database.GetBackupQueue().ToList();
        if (batchLimit is > 0)
        {
            queue = queue.Take(batchLimit.Value).ToList();
        }

        if (queue.Count == 0)
        {
            SetBusy(false, "No pending or failed chats to back up. Run Discover All Chats first.");
            return;
        }

        _pauseRequested = false;
        SetBusy(true, $"Backing up {queue.Count} pending chats...");
        SetPauseEnabled(true);
        var backedUp = 0;
        var failed = 0;
        var stoppedByRateLimit = false;

        foreach (var candidate in queue)
        {
            if (_pauseRequested)
            {
                break;
            }

            var chat = new ChatSummary(candidate.Id, candidate.Title, candidate.Url);
            try
            {
                _database.MarkBackupStarted(chat.Id);
                RefreshStats();
                SetBusy(true, $"Opening {backedUp + failed + 1} of {queue.Count}: {chat.Title}");

                await NavigateAndWaitAsync(chat.Url);
                await ThrowIfRateLimitedAsync(chat);
                await WaitForChatReadyAsync();
                await ThrowIfRateLimitedAsync(chat);

                if (await BackupCurrentChatAsync(chat))
                {
                    backedUp++;
                    await DelayBetweenChatsAsync(queue.Count, backedUp + failed);
                }
                else
                {
                    failed++;
                    _database.MarkBackupFailed(chat, "No messages detected.");
                }
            }
            catch (RateLimitDetectedException ex)
            {
                stoppedByRateLimit = true;
                _pauseRequested = true;
                _database.MarkBackupDeferred(chat, ex.Message);
                SetBusy(true, "Rate limit detected. Stopped backup and left the current chat pending.");
                break;
            }
            catch (Exception ex)
            {
                failed++;
                _database.MarkBackupFailed(chat, ex.Message);
                SetBusy(true, $"Failed {chat.Title}: {ex.Message}");
                await Task.Delay(800);
            }

            RefreshStats();
        }

        SetPauseEnabled(false);
        RefreshResults();
        RefreshStats();

        var status = stoppedByRateLimit
            ? $"Stopped by ChatGPT rate limit after saving {backedUp} chats. Wait a few minutes, then resume."
            : _pauseRequested
            ? $"Paused after saving {backedUp} chats. Failed this run: {failed}."
            : $"Finished. Saved {backedUp} chats. Failed this run: {failed}.";

        SetBusy(false, status);
    }

    private int? GetBackupBatchLimit()
    {
        if (BackupAllCheckBox.IsChecked == true)
        {
            return null;
        }

        if (int.TryParse(BatchSizeBox.Text.Trim(), out var value) && value > 0)
        {
            return value;
        }

        BatchSizeBox.Text = "25";
        return 25;
    }

    private async Task<bool> BackupCurrentChatAsync(ChatSummary? knownChat = null)
    {
        SetBusy(true, "Extracting current chat...");
        await ThrowIfRateLimitedAsync(knownChat);
        var extracted = await ExtractChatFromBackendAsync();
        if (extracted is null)
        {
            _lastExtractionMethod = "DOM";
            extracted = await ExtractChatWithScrollAsync();
        }

        if (extracted is null || extracted.Messages.Count == 0)
        {
            SetBusy(false, "No messages were detected on the current page.");
            return false;
        }

        var chat = knownChat ?? new ChatSummary(
            extracted.Id,
            string.IsNullOrWhiteSpace(extracted.Title) ? "Untitled chat" : extracted.Title,
            extracted.Url);

        var messages = extracted.Messages
            .Select((message, index) => new ChatMessage(message.Role, message.Text, index))
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .ToList();

        _database.SaveChat(chat, messages, extracted.RawHtml);
        RefreshResults();
        RefreshStats();
        SetBusy(false, $"Saved {messages.Count} messages from \"{chat.Title}\" via {_lastExtractionMethod}.");
        return true;
    }

    private async Task<ExtractedChat?> ExtractChatFromBackendAsync()
    {
        var json = await Browser.CoreWebView2.ExecuteScriptAsync(ChatBackendExtractScript);
        var extracted = DeserializeScriptResult<ExtractedChat>(json);

        if (extracted is { Messages.Count: > 0 })
        {
            _lastExtractionMethod = "backend";
            return extracted;
        }

        return null;
    }

    private async Task<ExtractedChat?> ExtractChatWithScrollAsync()
    {
        var snapshots = new List<ExtractedChat>();

        async Task CollectSnapshotAsync()
        {
            var json = await Browser.CoreWebView2.ExecuteScriptAsync(ChatExtractScript);
            var snapshot = DeserializeScriptResult<ExtractedChat>(json);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        await CollectSnapshotAsync();

        for (var i = 0; i < 80; i++)
        {
            var movedJson = await Browser.CoreWebView2.ExecuteScriptAsync(ChatScrollUpScript);
            await Task.Delay(120);
            await CollectSnapshotAsync();

            if (!DeserializeScriptResult<bool>(movedJson))
            {
                break;
            }
        }

        for (var i = 0; i < 160; i++)
        {
            var movedJson = await Browser.CoreWebView2.ExecuteScriptAsync(ChatScrollDownScript);
            await Task.Delay(120);
            await CollectSnapshotAsync();

            if (!DeserializeScriptResult<bool>(movedJson))
            {
                break;
            }
        }

        var first = snapshots.FirstOrDefault(snapshot => snapshot.Messages.Count > 0);
        if (first is null)
        {
            return null;
        }

        var messages = snapshots
            .SelectMany(snapshot => snapshot.Messages)
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .DistinctBy(message => $"{message.Role}\u001f{NormalizeMessageText(message.Text)}")
            .ToList();

        var rawHtml = snapshots.LastOrDefault(snapshot => !string.IsNullOrWhiteSpace(snapshot.RawHtml))?.RawHtml ?? first.RawHtml;
        return first with { RawHtml = rawHtml, Messages = messages };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshResults();
    }

    private void RefreshResults()
    {
        var hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        var results = _database.Search(SearchBox.Text);
        ResultsList.ItemsSource = results
            .Select(result => result with
            {
                Text = hasQuery
                    ? $"{result.Title} | {result.Role}: {TrimForList(result.Text)}"
                    : $"{result.MessageCount} saved messages"
            })
            .ToList();
    }

    private void RefreshStats()
    {
        var stats = _database.GetStats();
        StatsText.Text = $"Discovered: {stats.Discovered} | Saved: {stats.Saved} | Pending: {stats.Pending} | Failed: {stats.Failed}";
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedResult = ResultsList.SelectedItem as SearchResult;
    }

    private async void ResultsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_selectedResult is null)
        {
            return;
        }

        SetBusy(true, $"Opening \"{_selectedResult.Title}\"...");
        try
        {
            await NavigateAndWaitAsync(_selectedResult.Url);
            SetBusy(false, $"Opened \"{_selectedResult.Title}\".");
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Open failed: {ex.Message}");
        }
    }

    private async void RefreshSelectedChatMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedResult is null)
        {
            SetBusy(false, "Select a saved chat or search result first.");
            return;
        }

        var chat = new ChatSummary(_selectedResult.ChatId, _selectedResult.Title, _selectedResult.Url);
        try
        {
            SetBusy(true, $"Refreshing \"{chat.Title}\" from ChatGPT...");
            await NavigateAndWaitAsync(chat.Url);
            await ThrowIfRateLimitedAsync(chat);
            await WaitForChatReadyAsync();
            await ThrowIfRateLimitedAsync(chat);
            await BackupCurrentChatAsync(chat);
        }
        catch (RateLimitDetectedException ex)
        {
            _database.MarkBackupDeferred(chat, ex.Message);
            SetBusy(false, "Rate limit detected. Refresh stopped and left the chat pending.");
        }
        catch (Exception ex)
        {
            _database.MarkBackupFailed(chat, ex.Message);
            SetBusy(false, $"Refresh failed: {ex.Message}");
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        ExportButton.ContextMenu.PlacementTarget = ExportButton;
        ExportButton.ContextMenu.IsOpen = true;
    }

    private void ExportTranscriptMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ExportTranscript();
    }

    private void ExportContinuationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ExportContinuation();
    }

    private void ExportTranscript()
    {
        if (_selectedResult is null)
        {
            SetBusy(false, "Select a search result first.");
            return;
        }

        var markdown = _database.ExportMarkdown(_selectedResult.ChatId);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            SetBusy(false, "Could not export the selected chat.");
            return;
        }

        var exportFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "GPT Backups");

        Directory.CreateDirectory(exportFolder);
        var fileName = string.Join("_", _selectedResult.Title.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = _selectedResult.ChatId;
        }

        var path = Path.Combine(exportFolder, $"{fileName}.md");
        File.WriteAllText(path, markdown);
        OpenFile(path);
        SetBusy(false, $"Exported to {path}");
    }

    private void ContinuationExportButton_Click(object sender, RoutedEventArgs e)
    {
        ExportContinuation();
    }

    private void ExportContinuation()
    {
        if (_selectedResult is null)
        {
            SetBusy(false, "Select a saved chat or search result first.");
            return;
        }

        var markdown = _database.ExportContinuationMarkdown(_selectedResult.ChatId);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            SetBusy(false, "Could not export the selected chat.");
            return;
        }

        var exportFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "GPT Backups",
            "Continuations");

        Directory.CreateDirectory(exportFolder);
        var fileName = MakeSafeFileName(_selectedResult.Title);
        var path = Path.Combine(exportFolder, $"{fileName} - continue.md");
        File.WriteAllText(path, markdown);
        OpenFile(path);

        Clipboard.SetText(path);

        MessageBox.Show(
            $"Chat handoff file path copied to clipboard.\n\nUpload this Markdown file into a new ChatGPT chat, then ask ChatGPT to use it as context to continue the conversation.\n\n{path}",
            "Chat Handoff Ready",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        SetBusy(false, $"Chat handoff path copied: {path}");
    }

    private async Task WaitForNavigationSettledAsync()
    {
        var completion = new TaskCompletionSource();

        void Handler(object? _, CoreWebView2NavigationCompletedEventArgs __)
        {
            Browser.CoreWebView2.NavigationCompleted -= Handler;
            completion.TrySetResult();
        }

        Browser.CoreWebView2.NavigationCompleted += Handler;
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private async Task NavigateAndWaitAsync(string url)
    {
        var completion = new TaskCompletionSource();

        void Handler(object? _, CoreWebView2NavigationCompletedEventArgs __)
        {
            Browser.CoreWebView2.NavigationCompleted -= Handler;
            completion.TrySetResult();
        }

        Browser.CoreWebView2.NavigationCompleted += Handler;
        Browser.CoreWebView2.Navigate(url);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private async Task WaitForChatReadyAsync()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await ThrowIfRateLimitedAsync(null);
            var json = await Browser.CoreWebView2.ExecuteScriptAsync(ChatReadyScript);
            if (DeserializeScriptResult<bool>(json))
            {
                await Task.Delay(500);
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("Chat did not finish loading.");
    }

    private async Task ThrowIfRateLimitedAsync(ChatSummary? chat)
    {
        var json = await Browser.CoreWebView2.ExecuteScriptAsync(RateLimitScript);
        var message = DeserializeScriptResult<string>(json);
        if (!string.IsNullOrWhiteSpace(message))
        {
            if (chat is not null)
            {
                _database.MarkBackupDeferred(chat, message);
            }

            throw new RateLimitDetectedException(message);
        }
    }

    private async Task DelayBetweenChatsAsync(int total, int completed)
    {
        var delaySeconds = GetDelaySeconds();
        if (delaySeconds <= 0 || completed >= total)
        {
            return;
        }

        SetBusy(true, $"Waiting {delaySeconds}s before next chat...");
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
    }

    private int GetDelaySeconds()
    {
        if (int.TryParse(DelaySecondsBox.Text.Trim(), out var value) && value >= 0)
        {
            return Math.Min(value, 300);
        }

        DelaySecondsBox.Text = "8";
        return 8;
    }

    private void SetBusy(bool isBusy, string message)
    {
        StatusText.Text = message;
        BackupCurrentButton.IsEnabled = !isBusy;
        ScanListButton.IsEnabled = !isBusy;
        BackupAllButton.IsEnabled = !isBusy;
        OpenChatGptButton.IsEnabled = !isBusy;
        RetryFailedButton.IsEnabled = !isBusy;
        BackupAllCheckBox.IsEnabled = !isBusy;
        BatchSizeBox.IsEnabled = !isBusy && BackupAllCheckBox.IsChecked != true;
        DelaySecondsBox.IsEnabled = !isBusy;
        ExportButton.IsEnabled = !isBusy;
        ContinuationExportButton.IsEnabled = !isBusy;
    }

    private void SetPauseEnabled(bool isEnabled)
    {
        PauseButton.IsEnabled = isEnabled;
    }

    private static string TrimForList(string text)
    {
        var collapsed = string.Join(" ", text.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 120 ? collapsed : collapsed[..120] + "...";
    }

    private static string NormalizeMessageText(string text)
    {
        return string.Join(" ", text.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
    }

    private static string MakeSafeFileName(string title)
    {
        var fileName = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
        return string.IsNullOrWhiteSpace(fileName) ? "chat" : fileName;
    }

    private static void OpenFile(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static T? DeserializeScriptResult<T>(string scriptResult)
    {
        var json = JsonSerializer.Deserialize<string>(scriptResult);
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private sealed record ExtractedChat(
        string Id,
        string Title,
        string Url,
        string RawHtml,
        List<ExtractedMessage> Messages);

    private sealed record ExtractedMessage(string Role, string Text);

    private sealed class RateLimitDetectedException(string message) : Exception(message);

    private const string ChatListScript = """
        (() => {
          const links = [...document.querySelectorAll('a[href*="/c/"]')];
          const origin = location.origin;
          const chats = links.map(link => {
            const href = link.getAttribute('href') || '';
            const url = href.startsWith('http') ? href : origin + href;
            const id = (url.match(/\/c\/([^/?#]+)/) || [])[1] || url;
            const title = (link.innerText || link.getAttribute('aria-label') || 'Untitled chat').trim();
            return { id, title, url };
          }).filter(chat => chat.id && chat.title);
          return JSON.stringify(chats);
        })();
        """;

    private const string ChatSidebarScrollScript = """
        (() => {
          const clickLoadMore = () => {
            const candidates = [...document.querySelectorAll('button, a, [role="button"]')];
            const target = candidates.find(node => {
              const text = (node.innerText || node.getAttribute('aria-label') || '').trim().toLowerCase();
              return text === 'show more' || text === 'load more' || text.includes('show more');
            });

            if (target) {
              target.click();
              return true;
            }

            return false;
          };

          const links = [...document.querySelectorAll('a[href*="/c/"]')];
          const hasClicked = clickLoadMore();

          const scrollables = [...document.querySelectorAll('nav, aside, main, div, section')]
            .filter(node => {
              const style = getComputedStyle(node);
              const canScroll = node.scrollHeight > node.clientHeight + 60;
              const allowsScroll = /(auto|scroll|overlay)/.test(style.overflowY + style.overflow);
              const containsChatLinks = links.some(link => node.contains(link));
              return canScroll && containsChatLinks && (allowsScroll || node.clientHeight < window.innerHeight);
            })
            .sort((a, b) => {
              const aArea = a.clientWidth * a.clientHeight;
              const bArea = b.clientWidth * b.clientHeight;
              return aArea - bArea;
            });

          const container = scrollables[0];

          if (!container) {
            window.scrollBy(0, Math.floor(window.innerHeight * 0.35));
            return JSON.stringify(hasClicked);
          }

          const before = container.scrollTop;
          container.scrollTop = Math.min(
            container.scrollTop + Math.max(180, Math.floor(container.clientHeight * 0.45)),
            container.scrollHeight
          );
          container.dispatchEvent(new Event('scroll', { bubbles: true }));
          return JSON.stringify(hasClicked || container.scrollTop !== before);
        })();
        """;

    private const string ChatReadyScript = """
        (() => {
          const hasMessages =
            document.querySelectorAll('[data-message-author-role]').length > 0 ||
            document.querySelectorAll('article').length > 0;
          const busy =
            document.querySelector('[aria-busy="true"]') ||
            document.querySelector('[data-testid*="loading"]');
          return JSON.stringify(Boolean(hasMessages && !busy));
        })();
        """;

    private const string ChatScrollUpScript = """
        (() => {
          const main = document.querySelector('main') || document.scrollingElement || document.documentElement;
          const scrollables = [...document.querySelectorAll('main, div, section')]
            .filter(node => node.scrollHeight > node.clientHeight + 80)
            .filter(node => node.querySelector('[data-message-author-role], article'));
          const container = scrollables.sort((a, b) => (b.clientHeight * b.clientWidth) - (a.clientHeight * a.clientWidth))[0] || main;
          const before = container.scrollTop ?? window.scrollY;
          if ('scrollTop' in container) {
            container.scrollTop = Math.max(0, container.scrollTop - Math.max(500, Math.floor(container.clientHeight * 0.8)));
            container.dispatchEvent(new Event('scroll', { bubbles: true }));
            return JSON.stringify(container.scrollTop !== before);
          }
          window.scrollBy(0, -Math.max(500, Math.floor(window.innerHeight * 0.8)));
          return JSON.stringify(window.scrollY !== before);
        })();
        """;

    private const string ChatScrollDownScript = """
        (() => {
          const main = document.querySelector('main') || document.scrollingElement || document.documentElement;
          const scrollables = [...document.querySelectorAll('main, div, section')]
            .filter(node => node.scrollHeight > node.clientHeight + 80)
            .filter(node => node.querySelector('[data-message-author-role], article'));
          const container = scrollables.sort((a, b) => (b.clientHeight * b.clientWidth) - (a.clientHeight * a.clientWidth))[0] || main;
          const before = container.scrollTop ?? window.scrollY;
          if ('scrollTop' in container) {
            container.scrollTop = Math.min(container.scrollHeight, container.scrollTop + Math.max(500, Math.floor(container.clientHeight * 0.8)));
            container.dispatchEvent(new Event('scroll', { bubbles: true }));
            return JSON.stringify(container.scrollTop !== before);
          }
          window.scrollBy(0, Math.max(500, Math.floor(window.innerHeight * 0.8)));
          return JSON.stringify(window.scrollY !== before);
        })();
        """;

    private const string RateLimitScript = """
        (() => {
          const text = document.body?.innerText || '';
          const hasRateLimit =
            text.includes('Too many requests') &&
            text.includes('temporarily limited access to your conversations');
          if (!hasRateLimit) return JSON.stringify('');
          const dialog = document.querySelector('[role="dialog"]') || document.body;
          return JSON.stringify((dialog.innerText || 'Too many requests').trim());
        })();
        """;

    private const string ChatBackendExtractScript = """
        (() => {
          const url = location.href;
          const id = (url.match(/\/c\/([^/?#]+)/) || [])[1];
          if (!id) return JSON.stringify(null);

          let data = null;
          try {
            const request = new XMLHttpRequest();
            request.open('GET', `/backend-api/conversation/${id}`, false);
            request.setRequestHeader('accept', 'application/json');
            request.send(null);

            if (request.status < 200 || request.status >= 300) {
              return JSON.stringify(null);
            }

            data = JSON.parse(request.responseText);
          } catch {
            return JSON.stringify(null);
          }

          const mapping = data.mapping || {};

          const partToText = part => {
            if (typeof part === 'string') return part;
            if (!part || typeof part !== 'object') return '';
            if (typeof part.text === 'string') return part.text;
            if (typeof part.name === 'string') return `[${part.name}]`;
            if (typeof part.file_name === 'string') return `[${part.file_name}]`;
            if (typeof part.url === 'string') return part.url;
            return '';
          };

          const messageToText = message => {
            const content = message?.content;
            if (!content) return '';
            if (typeof content.text === 'string') return content.text.trim();
            if (Array.isArray(content.parts)) {
              return content.parts.map(partToText).filter(Boolean).join('\n').trim();
            }
            if (typeof content.result === 'string') return content.result.trim();
            return '';
          };

          const chain = [];
          let node = mapping[data.current_node];
          const seen = new Set();
          while (node && !seen.has(node.id)) {
            seen.add(node.id);
            if (node.message) chain.push(node.message);
            node = mapping[node.parent];
          }
          chain.reverse();

          const fallbackMessages = Object.values(mapping)
            .map(node => node.message)
            .filter(Boolean)
            .sort((a, b) => (a.create_time || 0) - (b.create_time || 0));

          const sourceMessages = chain.length > 0 ? chain : fallbackMessages;
          const messages = sourceMessages
            .map(message => ({
              role: message.author?.role || 'message',
              text: messageToText(message)
            }))
            .filter(message =>
              message.text &&
              message.role !== 'system' &&
              message.role !== 'tool'
            );

          return JSON.stringify({
            id,
            title: data.title || document.querySelector('title')?.innerText?.replace(' - ChatGPT', '').trim() || id,
            url,
            rawHtml: JSON.stringify(data),
            messages
          });
        })();
        """;

    private const string ChatExtractScript = """
        (() => {
          const url = location.href;
          const id = (url.match(/\/c\/([^/?#]+)/) || [])[1] || 'current-chat';
          const title =
            document.querySelector('title')?.innerText?.replace(' - ChatGPT', '').trim() ||
            document.querySelector('h1')?.innerText?.trim() ||
            id;

          const roleNodes = [...document.querySelectorAll('[data-message-author-role]')];
          let messages = roleNodes.map(node => ({
            role: node.getAttribute('data-message-author-role') || 'message',
            text: (node.innerText || '').trim()
          })).filter(message => message.text);

          if (messages.length === 0) {
            const articles = [...document.querySelectorAll('article')];
            messages = articles.map((node, index) => ({
              role: index % 2 === 0 ? 'user' : 'assistant',
              text: (node.innerText || '').trim()
            })).filter(message => message.text);
          }

          const main = document.querySelector('main') || document.body;
          return JSON.stringify({
            id,
            title,
            url,
            rawHtml: main.innerHTML,
            messages
          });
        })();
        """;
}
