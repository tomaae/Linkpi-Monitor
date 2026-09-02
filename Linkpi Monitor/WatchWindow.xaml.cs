using System.Windows;
using System.Windows.Media;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Linkpi_Monitor;

public partial class WatchWindow : Window
{
    private static readonly Brush OnlineBrush = CreateBrush("#39D98A");
    private static readonly Brush WarningBrush = CreateBrush("#F5B82E");
    private static readonly Brush OfflineBrush = CreateBrush("#FF5C5C");
    private static readonly Brush AccentBrush = CreateBrush("#F5B82E");
    private static readonly Brush AccentTextBrush = CreateBrush("#1C1608");

    private readonly Uri _streamUri;
    private readonly LibVLC _libVlc;
    private readonly VlcMediaPlayer _mediaPlayer;
    private Media? _media;
    private bool _isClosing;
    private bool _isMuted;

    public WatchWindow(ChannelDisplay channel)
    {
        InitializeComponent();
        if (channel.WatchUri is null)
        {
            throw new ArgumentException("The selected channel does not advertise a watchable stream.", nameof(channel));
        }

        _streamUri = channel.WatchUri;
        Title = $"{channel.Name} — LinkPi Monitor";
        ChannelNameText.Text = channel.Name;
        StreamAddressText.Text = $"{_streamUri.Scheme.ToUpperInvariant()} stream · {_streamUri.Host}";

        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--rtsp-tcp", "--network-caching=300");
        _mediaPlayer = new VlcMediaPlayer(_libVlc) { Volume = 75 };
        VideoView.MediaPlayer = _mediaPlayer;

        _mediaPlayer.Opening += (_, _) => SetPlaybackStatus("Opening stream", WarningBrush);
        _mediaPlayer.Buffering += (_, args) => SetPlaybackStatus($"Buffering {args.Cache:0}%", WarningBrush);
        _mediaPlayer.Playing += (_, _) => SetPlaybackStatus("Playing", OnlineBrush);
        _mediaPlayer.Paused += (_, _) => SetPlaybackStatus("Paused", WarningBrush);
        _mediaPlayer.Stopped += (_, _) => SetPlaybackStatus("Stopped", WarningBrush);
        _mediaPlayer.EncounteredError += (_, _) => SetPlaybackStatus("Playback failed", OfflineBrush);
        _mediaPlayer.EndReached += (_, _) => SetPlaybackStatus("Stream ended", WarningBrush);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => StartPlayback();

    private void StartPlayback()
    {
        _mediaPlayer.Stop();
        _media?.Dispose();
        _media = new Media(_libVlc, _streamUri);
        _media.AddOption(":rtsp-tcp");
        _media.AddOption(":network-caching=300");
        _mediaPlayer.Play(_media);
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        _mediaPlayer.Mute = _isMuted;
        MuteButton.Content = _isMuted ? "Unmute" : "Mute";
        MuteButton.ToolTip = _isMuted ? "Restore audio for this live view" : "Mute this live view";

        if (_isMuted)
        {
            MuteButton.Background = AccentBrush;
            MuteButton.BorderBrush = AccentBrush;
            MuteButton.Foreground = AccentTextBrush;
        }
        else
        {
            MuteButton.ClearValue(BackgroundProperty);
            MuteButton.ClearValue(BorderBrushProperty);
            MuteButton.ClearValue(ForegroundProperty);
        }
    }

    private void SetPlaybackStatus(string text, Brush brush)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing)
            {
                return;
            }

            PlaybackStatusText.Text = text;
            PlaybackStatusDot.Fill = brush;
        });
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosing = true;
        VideoView.MediaPlayer = null;
        _mediaPlayer.Stop();
        _media?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }

    private static Brush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
