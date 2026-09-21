# KHost.Plugins.Chromecast

Sends the song to a Chromecast receiver on the local network.

This is a KHost **display provider**: somewhere the song comes out that is not a screen. A screen
registers itself with the host over IPC and the host drives it; a receiver is the other way round,
so the host holds no handle on the device and asks this plugin for everything.

## Why it is a plugin

It used to be `src/KHost.Cast` in the host. Casting is not supported at the level the rest of the
app is, and mDNS browsing plus a CASTV2 protobuf transport (Sharpcaster, Zeroconf, Google.Protobuf,
System.Reactive) is a dependency chain the host should not carry just to play a local file. Moving
it out took all of that with it.

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

The build drops itself into a sibling KHost checkout's `plugins/khost.chromecast` when one exists.
Restart the host to load it — nothing installs into a running host.

The contracts come from NuGet (`KHost.Abstractions`, `KHost.Common`), not from a checkout beside
this one. While they are unreleased, pack them from the host repo with `./build/pack-contracts.sh`
and register the local feed once:

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
