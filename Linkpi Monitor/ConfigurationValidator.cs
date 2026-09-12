using System.Globalization;

namespace Linkpi_Monitor;

internal static class ConfigurationValidator
{
    private const int MaximumNameLength = 128;

    public static IReadOnlyList<string> Validate(ChannelConfiguration configuration)
    {
        var errors = new List<string>();
        RequiredText(errors, "Channel name", configuration.General.Name, MaximumNameLength);

        ValidateEncoder(errors, "Main encoder", configuration.MainEncoder);
        ValidateEncoder(errors, "Sub encoder", configuration.SubEncoder);
        Integer(errors, "Audio bitrate", configuration.Audio.Bitrate, 8, 1024, allowEmpty: true);

        if (configuration.Input.IsHdmi)
        {
            Integer(errors, "Input contrast", configuration.Input.Contrast, 0, 63);
            Crop(errors, "Input", configuration.Input.CropLeft, configuration.Input.CropTop,
                configuration.Input.CropRight, configuration.Input.CropBottom);
        }

        if (configuration.Input.IsUsbCamera)
        {
            VideoSize(errors, "USB capture size", configuration.Input.CaptureSize);
            Integer(errors, "USB capture framerate", configuration.Input.Framerate, 1, 240);
        }

        if (configuration.Decode.IsNetworkSource)
        {
            AbsoluteUri(errors, "Network source URL", configuration.Decode.SourceUrl, required: true);
            Integer(errors, "Input framerate", configuration.Decode.InputFramerate, -1, 240);
            Integer(errors, "Minimum delay", configuration.Decode.MinimumDelay, 0, 120_000);
            Integer(errors, "Decode contrast", configuration.Decode.Contrast, 0, 63);
            Crop(errors, "Decode", configuration.Decode.CropLeft, configuration.Decode.CropTop,
                configuration.Decode.CropRight, configuration.Decode.CropBottom);
        }

        ValidateStream(errors, "Main stream", configuration.MainStream);
        ValidateStream(errors, "Sub stream", configuration.SubStream);
        Integer(errors, "HLS segment length", configuration.Hls.SegmentLength, 1, 86_400, allowEmpty: true);
        Integer(errors, "HLS list length", configuration.Hls.ListLength, 1, 100_000, allowEmpty: true);
        Integer(errors, "MPEG-TS packet size", configuration.Transport.PacketSize, 188, 65_535);
        Integer(errors, "MPEG-TS PID", configuration.Transport.Pid, 0, 8_191, allowEmpty: true);
        Integer(errors, "MPEG-TS PMT PID", configuration.Transport.PmtPid, 0, 8_191, allowEmpty: true);
        Integer(errors, "MPEG-TS service ID", configuration.Transport.ServiceId, 0, 65_535, allowEmpty: true);
        Integer(errors, "MPEG-TS stream ID", configuration.Transport.StreamId, 0, 65_535, allowEmpty: true);
        Integer(errors, "MPEG-TS network ID", configuration.Transport.NetworkId, 0, 65_535, allowEmpty: true);
        return errors;
    }

    public static IReadOnlyList<string> Validate(PushConfiguration configuration)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < configuration.Destinations.Count; index++)
        {
            var destination = configuration.Destinations[index];
            var prefix = $"Destination {index + 1}";
            RequiredText(errors, $"{prefix} name", destination.Name, MaximumNameLength);
            if (!string.IsNullOrWhiteSpace(destination.Name) && !names.Add(destination.Name.Trim()))
            {
                errors.Add($"{prefix} name must be unique.");
            }

            if (destination.Enabled)
            {
                RequiredText(errors, $"{prefix} video source", destination.VideoSource, 64);
                AbsoluteUri(errors, $"{prefix} URL", destination.Url, required: true);
            }
            else
            {
                AbsoluteUri(errors, $"{prefix} URL", destination.Url, required: false);
            }
        }

        return errors;
    }

    public static IReadOnlyList<string> Validate(HardwareConfiguration configuration)
    {
        var errors = new List<string>();
        if (configuration.HasUsbAudioInput)
        {
            Integer(errors, "USB noise-reduction level", configuration.UsbAudioInput.NoiseReductionLevel, 1, 16);
        }
        if (configuration.HasLineAudio)
        {
            Integer(errors, "Line-input noise-reduction level", configuration.LineAudioInput.NoiseReductionLevel, 1, 16);
        }
        foreach (var output in configuration.VideoOutputs)
        {
            Integer(errors, $"{output.Name} luma", output.Luma, 0, 100);
            Integer(errors, $"{output.Name} contrast", output.Contrast, 0, 100);
            Integer(errors, $"{output.Name} saturation", output.Saturation, 0, 100);
            Integer(errors, $"{output.Name} hue", output.Hue, 0, 100);
        }
        return errors;
    }

    public static string Format(IReadOnlyList<string> errors) =>
        "Please correct the following values before saving:\n\n• " + string.Join("\n• ", errors);

    private static void ValidateEncoder(List<string> errors, string name, EncoderConfiguration encoder)
    {
        VideoSize(errors, $"{name} video size", encoder.VideoSize, allowAutomatic: true);
        Integer(errors, $"{name} bitrate", encoder.Bitrate, 1, 100_000, allowEmpty: !encoder.Enabled);
        Integer(errors, $"{name} framerate", encoder.Framerate, -1, 240);
        Integer(errors, $"{name} GOP", encoder.Gop, 1, 600);
        Integer(errors, $"{name} minimum QP", encoder.MinimumQp, 0, 51);
        Integer(errors, $"{name} maximum QP", encoder.MaximumQp, 0, 51);
        Integer(errors, $"{name} fixed I QP", encoder.FixedIQp, 0, 51);
        Integer(errors, $"{name} fixed P QP", encoder.FixedPQp, 0, 51);
        if (TryInteger(encoder.MinimumQp, out var minimum) &&
            TryInteger(encoder.MaximumQp, out var maximum) && minimum > maximum)
        {
            errors.Add($"{name} minimum QP cannot exceed maximum QP.");
        }
    }

    private static void ValidateStream(List<string> errors, string name, StreamOutputConfiguration stream)
    {
        if (stream.Srt.Enabled)
        {
            Integer(errors, $"{name} SRT port", stream.Srt.Port, 1, 65_535);
            Integer(errors, $"{name} SRT latency", stream.Srt.Latency, 0, 120_000);
        }
        if (stream.Udp.Enabled)
        {
            RequiredText(errors, $"{name} UDP address", stream.Udp.IpAddress, 255);
            Integer(errors, $"{name} UDP port", stream.Udp.Port, 1, 65_535);
            Integer(errors, $"{name} UDP TTL", stream.Udp.Ttl, 1, 255);
            Integer(errors, $"{name} UDP bandwidth", stream.Udp.Bandwidth, 0, 100);
        }
        if (stream.Rist.Enabled)
        {
            RequiredText(errors, $"{name} RIST address", stream.Rist.IpAddress, 255);
            Integer(errors, $"{name} RIST port", stream.Rist.Port, 1, 65_535);
        }
        if (stream.Push.Enabled)
        {
            AbsoluteUri(errors, $"{name} Push URL", stream.Push.Url, required: true);
            Integer(errors, $"{name} HEVC ID", stream.Push.HevcId, 0, 255);
        }
    }

    private static void Crop(List<string> errors, string name, params string[] values)
    {
        var labels = new[] { "left crop", "top crop", "right crop", "bottom crop" };
        for (var index = 0; index < values.Length; index++)
        {
            Integer(errors, $"{name} {labels[index]}", values[index], 0, 16_384);
        }
    }

    private static void VideoSize(List<string> errors, string name, string value, bool allowAutomatic = false)
    {
        if (allowAutomatic && value == "-1x-1") return;
        var parts = value.Split('x', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TryInteger(parts[0], out var width) || !TryInteger(parts[1], out var height) ||
            width is < 1 or > 16_384 || height is < 1 or > 16_384)
        {
            errors.Add($"{name} must use WIDTHxHEIGHT with positive values up to 16384.");
        }
    }

    private static void Integer(
        List<string> errors,
        string name,
        string value,
        int minimum,
        int maximum,
        bool allowEmpty = false)
    {
        if (allowEmpty && string.IsNullOrWhiteSpace(value)) return;
        if (!TryInteger(value, out var number) || number < minimum || number > maximum)
        {
            errors.Add($"{name} must be a whole number from {minimum} to {maximum}.");
        }
    }

    private static bool TryInteger(string value, out int number) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

    private static void RequiredText(List<string> errors, string name, string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{name} is required.");
        else if (value.Length > maximumLength) errors.Add($"{name} cannot exceed {maximumLength} characters.");
    }

    private static void AbsoluteUri(List<string> errors, string name, string value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required) errors.Add($"{name} is required.");
            return;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Scheme))
        {
            errors.Add($"{name} must be an absolute URL.");
        }
    }
}
