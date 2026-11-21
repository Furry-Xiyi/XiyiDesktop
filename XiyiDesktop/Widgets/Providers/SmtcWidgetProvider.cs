using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;
using System.Text.Json;
using Windows.UI.ViewManagement;

namespace XiyiDesktop.Widgets.Providers;

/// <summary>Windows Widgets provider for controlling SMTC sessions exposed by other apps.</summary>
internal sealed partial class SmtcWidgetProvider : IWidgetProvider
{
    private const string DefinitionId = "SmtcMediaControl";
    private const int ErrorDeviceNotReady = unchecked((int)0x80070015);
    private static readonly TimeSpan ActiveRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, WidgetState> Widgets = new();
    private static readonly ConcurrentDictionary<string, string> ActionIconDataUris = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object InitializationLock = new();
    private static readonly ManualResetEventSlim NoWidgetsRemaining = new(false);
    private static readonly ResourceLoader Resources = new();
    private static GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private static GlobalSystemMediaTransportControlsSession? _observedSession;
    private static TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _mediaPropertiesChangedHandler;
    private static TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _playbackInfoChangedHandler;
    private static UISettings? _uiSettings;
    private static Timer? _refreshTimer;
    private static Task? _initializationTask;

    public SmtcWidgetProvider()
    {
        EnsureInitialized();
        try
        {
            foreach (var widgetInfo in WidgetManager.GetDefault().GetWidgetInfos())
            {
                var context = widgetInfo.WidgetContext;
                if (string.Equals(context.DefinitionId, DefinitionId, StringComparison.OrdinalIgnoreCase))
                {
                    Widgets.TryAdd(context.Id, new WidgetState(context.Id, context.IsActive));
                    NoWidgetsRemaining.Reset();
                    _ = UpdateWidgetAsync(context.Id);
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to restore SMTC widgets: {exception}");
        }
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        if (!IsSupportedWidget(widgetContext))
            return;

        Widgets[widgetContext.Id] = new WidgetState(widgetContext.Id, widgetContext.IsActive);
        NoWidgetsRemaining.Reset();
        _ = UpdateWidgetAsync(widgetContext.Id);
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        Widgets.TryRemove(widgetId, out _);
        if (Widgets.IsEmpty)
            NoWidgetsRemaining.Set();
    }

    public void Activate(WidgetContext widgetContext)
    {
        if (Widgets.TryGetValue(widgetContext.Id, out var state))
        {
            state.IsActive = true;
            _ = UpdateWidgetAsync(widgetContext.Id);
        }
    }

    public void Deactivate(string widgetId)
    {
        if (Widgets.TryGetValue(widgetId, out var state))
            state.IsActive = false;
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        var context = contextChangedArgs.WidgetContext;
        if (Widgets.TryGetValue(context.Id, out var state))
        {
            state.IsActive = context.IsActive;
            _ = UpdateWidgetAsync(context.Id);
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        var widgetId = actionInvokedArgs.WidgetContext.Id;
        var verb = actionInvokedArgs.Verb?.ToLowerInvariant();
        _ = RunActionAsync(widgetId, verb);
    }

    private static async Task RunActionAsync(string widgetId, string? verb)
    {
        try
        {
            var manager = await GetSessionManagerAsync();
            var session = manager?.GetCurrentSession();
            if (session is not null)
            {
                switch (verb)
                {
                    case "previous":
                        await session.TrySkipPreviousAsync();
                        break;
                    case "next":
                        await session.TrySkipNextAsync();
                        break;
                    case "playpause":
                        var playbackInfo = session.GetPlaybackInfo();
                        if (playbackInfo.Controls.IsPlayPauseToggleEnabled)
                            await session.TryTogglePlayPauseAsync();
                        else if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            await session.TryPauseAsync();
                        else
                            await session.TryPlayAsync();
                        break;
                    case "play":
                        await session.TryPlayAsync();
                        break;
                    case "pause":
                        await session.TryPauseAsync();
                        break;
                    case "seekback":
                    case "seekforward":
                        var timeline = session.GetTimelineProperties();
                        var delta = TimeSpan.FromSeconds(15).Ticks * (verb == "seekback" ? -1 : 1);
                        var target = Math.Clamp(
                            timeline.Position.Ticks + delta,
                            timeline.StartTime.Ticks,
                            Math.Max(timeline.StartTime.Ticks, timeline.EndTime.Ticks));
                        await session.TryChangePlaybackPositionAsync(target);
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to invoke SMTC action '{verb}': {exception}");
        }

        if (Widgets.ContainsKey(widgetId))
            await UpdateWidgetAsync(widgetId);
    }

    private static async Task UpdateWidgetAsync(string widgetId)
    {
        if (!Widgets.TryGetValue(widgetId, out var state))
            return;

        if (!await state.UpdateLock.WaitAsync(0))
            return;

        try
        {
            var manager = await GetSessionManagerAsync();
            GlobalSystemMediaTransportControlsSession? session = null;
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback = null;
            GlobalSystemMediaTransportControlsSessionPlaybackControls? controls = null;
            GlobalSystemMediaTransportControlsSessionMediaProperties? properties = null;
            GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = null;
            string? playerName = null;

            try
            {
                session = manager?.GetCurrentSession();
                if (session is not null)
                {
                    playback = session.GetPlaybackInfo();
                    controls = playback.Controls;
                    properties = await session.TryGetMediaPropertiesAsync();
                    timeline = session.GetTimelineProperties();
                    playerName = SmtcSourceNameResolver.Resolve(session.SourceAppUserModelId);
                }
            }
            catch (COMException exception) when (exception.HResult == ErrorDeviceNotReady)
            {
                // The session can disappear or its media endpoint can temporarily
                // become unavailable while SMTC is returning asynchronous data.
                session = null;
                playback = null;
                controls = null;
                properties = null;
                timeline = null;
                playerName = null;
            }

            var title = FirstNonEmpty(properties?.Title, properties?.Subtitle) ?? Text("SMTC_NoMedia");
            var artist = FirstNonEmpty(properties?.Artist, properties?.AlbumArtist);
            var customState = session?.SourceAppUserModelId ?? string.Empty;
            var position = timeline is null || timeline.EndTime <= TimeSpan.Zero
                ? null
                : string.Format(Text("SMTC_PositionFormat"), FormatTime(timeline.Position), FormatTime(timeline.EndTime));
            var card = BuildCard(title, artist, playerName, position, controls, playback?.PlaybackStatus);
            if (string.Equals(card, state.LastTemplate, StringComparison.Ordinal) &&
                string.Equals(customState, state.LastCustomState, StringComparison.Ordinal))
            {
                return;
            }

            var options = new WidgetUpdateRequestOptions(widgetId)
            {
                Template = card,
                Data = "{}",
                CustomState = customState
            };
            WidgetManager.GetDefault().UpdateWidget(options);
            state.LastTemplate = card;
            state.LastCustomState = customState;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to update SMTC widget: {exception}");
        }
        finally
        {
            state.UpdateLock.Release();
        }
    }

    private static string BuildCard(
        string title,
        string? artist,
        string? playerName,
        string? position,
        GlobalSystemMediaTransportControlsSessionPlaybackControls? controls,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus)
    {
        var isPlaying = playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var canPrevious = controls?.IsPreviousEnabled == true;
        var canNext = controls?.IsNextEnabled == true;
        var canToggle = controls?.IsPlayPauseToggleEnabled == true ||
            controls?.IsPlayEnabled == true || controls?.IsPauseEnabled == true;
        var canChangePosition = controls?.IsPlaybackPositionEnabled == true;
        var theme = IsDarkMode() ? "Dark" : "Light";
        var headerText = string.IsNullOrWhiteSpace(artist) ? title : $"{title} · {artist}";

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WritePropertyName("header");
        writer.WriteStartObject();
        writer.WriteString("text", headerText);
        writer.WriteString("iconUrl", $"ms-appx:///Assets/SMTCWidget/{theme}/SMTCWidget.png");
        writer.WriteBoolean("moveTitleToHeader", true);
        writer.WriteNumber("promotedBodyTextBlocks", string.IsNullOrWhiteSpace(artist) ? 1 : 2);
        writer.WriteBoolean("smtcCircularActions", true);
        writer.WriteEndObject();
        writer.WriteString("$schema", "http://adaptivecards.io/schemas/adaptive-card.json");
        writer.WriteString("type", "AdaptiveCard");
        writer.WriteString("version", "1.5");
        writer.WritePropertyName("body");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("type", "Container");
        writer.WriteString("height", "stretch");
        writer.WriteString("verticalContentAlignment", "top");
        writer.WritePropertyName("items");
        writer.WriteStartArray();
        WriteTextBlock(writer, title, "large", isBold: true);
        if (!string.IsNullOrWhiteSpace(artist))
            WriteTextBlock(writer, artist, "small", isSubtle: true, spacing: "small");

        if (!string.IsNullOrWhiteSpace(playerName))
            WriteTextBlock(writer, playerName, "small", isSubtle: true, spacing: "small");
        if (!string.IsNullOrWhiteSpace(position))
            WriteTextBlock(writer, position, "small", isSubtle: true, spacing: "small");

        writer.WriteStartObject();
        writer.WriteString("type", "Container");
        writer.WriteString("height", "stretch");
        writer.WriteString("verticalContentAlignment", "center");
        writer.WritePropertyName("items");
        writer.WriteStartArray();
        WriteCenteredActions(writer, isPlaying, canPrevious, canToggle, canNext, canChangePosition, theme);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCenteredActions(
        Utf8JsonWriter writer,
        bool isPlaying,
        bool canPrevious,
        bool canToggle,
        bool canNext,
        bool canChangePosition,
        string theme)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "ColumnSet");
        writer.WriteString("horizontalAlignment", "center");
        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("type", "Column");
        writer.WriteString("width", "auto");
        writer.WriteString("horizontalAlignment", "center");
        writer.WritePropertyName("items");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("type", "ActionSet");
        writer.WritePropertyName("actions");
        writer.WriteStartArray();
        WriteControlAction(writer, "seekback", "Rewind.png", Text("SMTC_SeekBack"), theme, canChangePosition);
        WriteControlAction(writer, "previous", "Previous.png", Text("SMTC_Previous"), theme, canPrevious);
        WriteControlAction(writer, "playpause", isPlaying ? "Pause.png" : "Play.png", isPlaying ? Text("SMTC_Pause") : Text("SMTC_Play"), theme, canToggle);
        WriteControlAction(writer, "next", "Next.png", Text("SMTC_Next"), theme, canNext);
        WriteControlAction(writer, "seekforward", "FastForward.png", Text("SMTC_SeekForward"), theme, canChangePosition);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteControlAction(
        Utf8JsonWriter writer,
        string verb,
        string assetName,
        string title,
        string theme,
        bool isEnabled)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Action.Execute");
        writer.WriteString("title", " ");
        writer.WriteString("tooltip", title);
        writer.WriteString("iconUrl", GetActionIconDataUri(theme, assetName));
        writer.WriteString("verb", verb);
        writer.WriteBoolean("isEnabled", isEnabled);
        writer.WriteString("style", "default");
        writer.WriteEndObject();
    }

    private static void WriteTextBlock(
        Utf8JsonWriter writer,
        string text,
        string size,
        bool isBold = false,
        bool isSubtle = false,
        string? spacing = null)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "TextBlock");
        writer.WriteString("text", text);
        writer.WriteString("size", size);
        if (isBold)
            writer.WriteString("weight", "bolder");
        if (isSubtle)
            writer.WriteBoolean("isSubtle", true);
        writer.WriteBoolean("wrap", true);
        if (spacing is not null)
            writer.WriteString("spacing", spacing);
        writer.WriteEndObject();
    }

    private static void EnsureInitialized()
    {
        lock (InitializationLock)
        {
            _initializationTask ??= InitializeAsync();
            _refreshTimer ??= new Timer(_ => RefreshWidgets(), null, IdleRefreshInterval, IdleRefreshInterval);
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionManager?> GetSessionManagerAsync()
    {
        EnsureInitialized();
        if (_initializationTask is not null)
            await _initializationTask;
        return _sessionManager;
    }

    private static async Task InitializeAsync()
    {
        try
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => RefreshWidgets();
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.SessionsChanged += (_, _) => RefreshWidgets();
            _sessionManager.CurrentSessionChanged += (_, _) =>
            {
                ObserveCurrentSession();
                RefreshWidgets();
            };
            ObserveCurrentSession();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to initialize SMTC manager: {exception}");
        }
    }

    private static void RefreshWidgets()
    {
        UpdateRefreshInterval();
        foreach (var widget in Widgets.Values.Where(widget => widget.IsActive))
            _ = UpdateWidgetAsync(widget.Id);
    }

    private static void UpdateRefreshInterval()
    {
        var interval = IdleRefreshInterval;
        try
        {
            if (_sessionManager?.GetCurrentSession()?.GetPlaybackInfo()?.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                interval = ActiveRefreshInterval;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to determine the SMTC refresh interval: {exception.Message}");
        }

        _refreshTimer?.Change(interval, interval);
    }

    private static string GetActionIconDataUri(string theme, string assetName)
    {
        var key = $"{theme}/{assetName}";
        return ActionIconDataUris.GetOrAdd(key, _ =>
        {
            var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SMTCWidget", theme, assetName);
            try
            {
                return $"data:image/png;base64,{Convert.ToBase64String(File.ReadAllBytes(assetPath))}";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Debug.WriteLine($"Unable to read SMTC action icon '{assetPath}': {exception.Message}");
                return $"ms-appx:///Assets/SMTCWidget/{theme}/{assetName}";
            }
        });
    }

    private static void ObserveCurrentSession()
    {
        try
        {
            var currentSession = _sessionManager?.GetCurrentSession();
            if (ReferenceEquals(currentSession, _observedSession))
                return;

            if (_observedSession is not null)
            {
                if (_mediaPropertiesChangedHandler is not null)
                    _observedSession.MediaPropertiesChanged -= _mediaPropertiesChangedHandler;
                if (_playbackInfoChangedHandler is not null)
                    _observedSession.PlaybackInfoChanged -= _playbackInfoChangedHandler;
            }

            _observedSession = currentSession;
            _mediaPropertiesChangedHandler = null;
            _playbackInfoChangedHandler = null;
            if (currentSession is null)
                return;

            _mediaPropertiesChangedHandler = (_, _) => RefreshWidgets();
            _playbackInfoChangedHandler = (_, _) => RefreshWidgets();
            currentSession.MediaPropertiesChanged += _mediaPropertiesChangedHandler;
            currentSession.PlaybackInfoChanged += _playbackInfoChangedHandler;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to observe the current SMTC session: {exception}");
        }
    }

    private static bool IsSupportedWidget(WidgetContext context) =>
        string.Equals(context.DefinitionId, DefinitionId, StringComparison.OrdinalIgnoreCase) &&
        context.Size is WidgetSize.Small or WidgetSize.Medium;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private static string Text(string key) => Resources.GetString(key);

    private static bool IsDarkMode()
    {
        try
        {
            if (_uiSettings is null)
                return false;

            var background = _uiSettings.GetColorValue(UIColorType.Background);
            var luminance = 0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B;
            return luminance < 128;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to determine the Windows widget color theme: {exception.Message}");
            return false;
        }
    }

    internal static void WaitForLastWidgetDeletion() => NoWidgetsRemaining.Wait();

    private sealed class WidgetState(string id, bool isActive)
    {
        public string Id { get; } = id;
        public bool IsActive { get; set; } = isActive;
        public string? LastTemplate { get; set; }
        public string LastCustomState { get; set; } = string.Empty;
        public SemaphoreSlim UpdateLock { get; } = new(1, 1);
    }
}
