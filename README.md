# KHost.Plugins.Chromecast

Sends the song to a Chromecast receiver on the local network.

This is a KHost **display provider**: somewhere the song comes out that is not a screen. A screen
registers itself with the host over IPC and the host drives it; a receiver is the other way round,
so the host holds no handle on the device and asks this plugin for everything.

## Why it is a plugin

mDNS browsing plus a CASTV2 protobuf transport (Sharpcaster, Zeroconf, Google.Protobuf,
System.Reactive) is a dependency chain the host should not carry just to play a local file, so
casting lives here.

## Installing

From a host, open Plugins, then Available, install Chromecast and restart KHost. The release is one
portable zip that runs on any platform. By hand, unzip a release into its own folder under KHost's
`plugins/`, enable it on the Plugins page and restart.

Settings: how long discovery sweeps the network (default 5 s) and how long a receiver has to answer
a connect (default 10 s). A save takes effect on the next sweep or connect, with no restart: the host serves the settings as options.

## The device list

Discovery, connecting and disconnecting are reached from a **Devices** button on the Plugins page,
not from the console's Screens dialog. A plugin cannot ship markup, so `ChromecastDeviceTable`
describes a table — columns, a row per receiver, and what each button does — and the host draws it
from a `ShowPluginTableRequest`. The button also reports what the room is watching without being
opened.

## What it implements

`IDisplayProvider` — discovery, one connection at a time, and the transport calls playback drives it
with. It deliberately implements no screen interface: a receiver plays on its own schedule and can
never be held to the group's timeline, so it holds no role in the sync set.
`ChromecastProviderSeparationTests` fails if it ever accepts an `IScreenCommand`.

## Building

```bash
dotnet build KHost.Plugins.Chromecast.slnx
dotnet test KHost.Plugins.Chromecast.slnx
```

The contracts (`KHost.Abstractions`, `KHost.Common`, 0.58.0) are package references, not a checkout
beside this one. They are not on nuget.org yet: pack them from the KHost repo with
`./build/pack-contracts.sh` and register the local feed once:

```bash
dotnet nuget add source ~/.nuget/khost-local -n khost-local
```

Note the test project takes the contracts **transitively**, through its `ProjectReference` to the
plugin. Naming them as a `PackageReference` of its own would make the packages' `build/` targets
trim them from its output, and every test would then fail to load `KHost.Abstractions`.

## Tests

The unit tests need no network. The `ChromecastReceiverIntegrationTests` drive a real CASTV2
receiver and skip unless one is listening on `127.0.0.1:8009` — the
[Chromecast-Emulator](https://github.com/riddlemd/Chromecast-Emulator) serves that.
