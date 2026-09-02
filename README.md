# LinkPi Monitor

A read-only Windows monitor for LinkPi encoder devices, built with C# and .NET 10 WPF.

## Current features

- Select one of multiple configured LinkPi devices.
- Poll CPU, memory, temperature, channel configuration, physical-input state and push state.
- Present decode, encode and stream output details together for every channel.
- Keep HDMI, USB camera, file, color-key and Mix channels visible alongside network decoders.
- Show a current dashboard snapshot for every enabled, decodable channel.
- Watch an advertised RTSP stream directly inside LinkPi Monitor.
- Show publishing status and bitrate without exposing stream keys.
- Inspect and locally experiment with complete Decode, Encode and Stream configuration controls.

The configuration window populates its controls from the selected device, including separate main/sub video encoders and outputs, the shared audio encoder, network decode and picture transforms, protocol-specific settings, MPEG-TS, HLS and NDI. Controls are selectable for interface testing, but Save remains disabled and no configuration update is sent to the device.

Preview cards use the same `enc.snap` plus `snap/snap{id}.jpg` cycle as the device dashboard, but only once per configured monitor refresh rather than twice per second. Snapshot failures are isolated per channel. The app never invokes an update/start/stop method.

## Configuration

On first launch, the application creates `config.json` beside the executable when the file does not already exist. Edit the generated starter device and add further entries as needed. The real file is excluded from Git because it can contain credentials.

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

The Watch button opens the live feed in a dedicated embedded player window. The required LibVLC runtime is included in published packages; a separately installed media player is not required.

## Create a release package

From the repository root:

```powershell
.\Publish.ps1
```

The script restores the solution, runs the Release checks, publishes a framework-dependent Windows x64 single-file executable, audits the exact package contents, and creates:

```text
artifacts/LinkpiMonitor/
artifacts/LinkpiMonitor-win-x64.zip
```

The release never contains the credential-bearing `config.json`; the application creates it on first launch. Run the package from a folder where your account can create and update files. An interactive script waits for Enter before closing; automated callers can use `.\Publish.ps1 -NoPause`.
