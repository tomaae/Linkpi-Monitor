# LinkPi Monitor

A read-only Windows monitor for LinkPi encoder devices, built with C# and .NET 10 WPF.

## Current features

- Select one of multiple configured LinkPi devices.
- Poll CPU, memory, temperature, channel configuration, physical-input state and push state.
- Present decode, encode and stream output details together for every channel.
- Keep HDMI, USB camera, file, color-key and Mix channels visible alongside network decoders.
- Open an advertised RTSP or HTTP stream through the Windows registered media player.
- Show publishing status and bitrate without exposing stream keys.
- Display planned editing controls in a disabled state.

The device API has no passive JPEG snapshot method. Preview cards therefore use a placeholder until local stream-frame decoding is added. The app never invokes `enc.snap`, `push.getPreview`, or any update/start/stop method.

## Configuration

Copy `Linkpi Monitor/config.example.json` to `Linkpi Monitor/config.json`. The real file is excluded from Git because it contains credentials.

```json
{
  "RefreshIntervalSeconds": 5,
  "Devices": [
    {
      "Name": "LinkPi studio",
      "BaseUrl": "http://10.0.1.6",
      "Username": "admin",
      "Password": "replace-me"
    }
  ]
}
```

Only the selected device is polled. Existing legacy configuration using a single `LinkPi` object is still accepted.

## Run

```powershell
dotnet run --project '.\Linkpi Monitor\Linkpi Monitor.csproj'
```

The Watch button opens the preferred advertised RTSP stream. A player such as VLC must be installed and registered for the `rtsp` protocol.
