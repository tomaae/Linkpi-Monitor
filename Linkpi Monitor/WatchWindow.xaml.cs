using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private const double HeaderHeight = 64;
    private const double FallbackAspectRatio = 16d / 9d;
    private const double PreferredVideoHeight = 596;
    private const double PreferredVideoWidth = 1060;

    private readonly Uri _streamUri;
    private readonly LibVLC _libVlc;
    private readonly VlcMediaPlayer _mediaPlayer;
    private readonly bool _hasConfiguredAspectRatio;
    private Media? _media;
    private bool _isClosing;
    private bool _isMuted;
    private double _videoAspectRatio;

    public WatchWindow(ChannelDisplay channel)
    {
        InitializeComponent();
        if (channel.WatchUri is null)
        {
            throw new ArgumentException("The selected channel does not advertise a watchable stream.", nameof(channel));
        }

        _streamUri = channel.WatchUri;
        (_videoAspectRatio, _hasConfiguredAspectRatio) = GetInitialAspectRatio(channel);
        SizeWindowForAspectRatio();
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
        _mediaPlayer.Vout += (_, _) => RefreshAspectRatioFromPlayer();
        _mediaPlayer.Paused += (_, _) => SetPlaybackStatus("Paused", WarningBrush);
        _mediaPlayer.Stopped += (_, _) => SetPlaybackStatus("Stopped", WarningBrush);
        _mediaPlayer.EncounteredError += (_, _) => SetPlaybackStatus("Playback failed", OfflineBrush);
        _mediaPlayer.EndReached += (_, _) => SetPlaybackStatus("Stream ended", WarningBrush);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ResizeVideoToAspectRatio();
        StartPlayback();
    }

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

    private void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeVideoToAspectRatio();

    private void SizeWindowForAspectRatio()
    {
        var workArea = SystemParameters.WorkArea;
        var maximumWidth = Math.Min(PreferredVideoWidth, workArea.Width * 0.9);
        var maximumVideoHeight = Math.Min(PreferredVideoHeight, (workArea.Height * 0.9) - HeaderHeight);

        var videoWidth = maximumVideoHeight * _videoAspectRatio;
        var videoHeight = maximumVideoHeight;
        if (videoWidth > maximumWidth)
        {
            videoWidth = maximumWidth;
            videoHeight = videoWidth / _videoAspectRatio;
        }

        Width = Math.Max(MinWidth, videoWidth);
        Height = Math.Max(MinHeight, videoHeight + HeaderHeight);
    }

    private void ResizeVideoToAspectRatio()
    {
        if (VideoHost.ActualWidth <= 0 || VideoHost.ActualHeight <= 0 || _videoAspectRatio <= 0)
        {
            return;
        }

        var width = VideoHost.ActualWidth;
        var height = width / _videoAspectRatio;
        if (height > VideoHost.ActualHeight)
        {
            height = VideoHost.ActualHeight;
            width = height * _videoAspectRatio;
        }

        VideoView.Width = Math.Max(1, width);
        VideoView.Height = Math.Max(1, height);
    }

    private void RefreshAspectRatioFromPlayer()
    {
        uint width = 0;
        uint height = 0;
        if (_hasConfiguredAspectRatio || _isClosing || !_mediaPlayer.Size(0, ref width, ref height) || height == 0)
        {
            return;
        }

        var detectedAspectRatio = (double)width / height;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || detectedAspectRatio <= 0)
            {
                return;
            }

            _videoAspectRatio = detectedAspectRatio;
            SizeWindowForAspectRatio();
            ResizeVideoToAspectRatio();
        });
    }

    private static (double AspectRatio, bool IsConfigured) GetInitialAspectRatio(ChannelDisplay channel)
    {
        if (channel.SourceWidth > 0 && channel.SourceHeight > 0)
        {
            var decode = channel.Configuration.Decode;
            var croppedWidth = channel.SourceWidth - ParseCrop(decode.CropLeft) - ParseCrop(decode.CropRight);
            var croppedHeight = channel.SourceHeight - ParseCrop(decode.CropTop) - ParseCrop(decode.CropBottom);
            var width = croppedWidth > 0 ? croppedWidth : channel.SourceWidth;
            var height = croppedHeight > 0 ? croppedHeight : channel.SourceHeight;
            var rotation = ParseRotation(decode.Rotate);
            return rotation is 90 or 270
                ? ((double)height / width, true)
                : ((double)width / height, true);
        }

        if (channel.PreviewImage is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } preview)
        {
            return ((double)preview.PixelWidth / preview.PixelHeight, true);
        }

        var separator = channel.Configuration.MainEncoder.VideoSize.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (separator > 0 &&
            int.TryParse(channel.Configuration.MainEncoder.VideoSize[..separator], out var encodedWidth) &&
            int.TryParse(channel.Configuration.MainEncoder.VideoSize[(separator + 1)..], out var encodedHeight) &&
            encodedWidth > 0 && encodedHeight > 0)
        {
            return ((double)encodedWidth / encodedHeight, true);
        }

        return (FallbackAspectRatio, false);
    }

    private static int ParseCrop(string value) =>
        int.TryParse(value, out var parsed) ? Math.Max(0, parsed) : 0;

    private static int ParseRotation(string value) =>
        int.TryParse(value, out var parsed) ? ((parsed % 360) + 360) % 360 : 0;

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
