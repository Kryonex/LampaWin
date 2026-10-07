# Third-party software notices

LampaWin bundles third-party software. This project is not affiliated with or endorsed by the projects below.

## Lampa

- Project: [yumata/lampa-source](https://github.com/yumata/lampa-source)
- Pinned source revision: `b4a13b6af7fe2f3bbbcb91f4eb434ab3378f8d5d`
- License: GNU General Public License, version 2 (GPL-2.0)
- Copyright: Lampa contributors; see the source repository and `artifacts/source/LAMPA-LICENSE.txt`.
- The bundled web application is compiled from this revision. Complete corresponding source and build changes are provided in `artifacts/source`.

## Jackett

- Project: [Jackett/Jackett](https://github.com/Jackett/Jackett)
- Pinned source revision: `125f96b2aed180f377890433714f6a2879eb3be6` (v0.24.2798)
- License: GNU General Public License, version 2 (GPL-2.0)
- Copyright: Jackett contributors; see `artifacts/source/JACKETT-LICENSE.txt`.
- Windows data-folder ACL handling is patched to preserve inherited per-user profile permissions instead of granting WorldSid FullControl. The corresponding patch and pinned source are supplied in `artifacts/source`.

## TorrServer

- Project: [YouROK/TorrServer](https://github.com/YouROK/TorrServer)
- Bundled release: `MatriX.145.2`, Windows amd64
- License: GNU General Public License, version 3 (GPL-3.0); see the bundled license.
- Copyright: TorrServer contributors. The official release binary is bundled unchanged; source and license are supplied in the distribution.
- The corresponding source at commit `bb3f4468d38a96f4df563f28c9fd4ccc3b2b37a9` is included as `artifacts/source/torrserver-MatriX.145.2-source.zip`.

## .NET, WebView2 and VLC

The Windows x64 application is published self-contained with .NET 10.0. The runtime's applicable Microsoft software license and third-party notices are included as `licenses/dotnet-LICENSE.txt` and `licenses/dotnet-ThirdPartyNotices.txt`. `System.Security.Cryptography.ProtectedData` 10.0.0 is MIT-licensed; its package third-party notices are included as `licenses/System.Security.Cryptography.ProtectedData-THIRD-PARTY-NOTICES.txt`.

The managed playback bindings are `LibVLCSharp` 3.10.1 and `LibVLCSharp.WPF` 3.10.1. The bundled native playback engine is `VideoLAN.LibVLC.Windows` 3.0.24 (Windows x64). Their NuGet package metadata declares LGPL-2.1-or-later. The complete LGPL-2.1 license is included as `licenses/LGPL-2.1-or-later.txt`; package source and license metadata are available from [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) and [VideoLAN.LibVLC.Windows](https://code.videolan.org/videolan/libvlc-nuget). The bindings remain separate replaceable libraries and can be relinked by replacing the corresponding DLLs.

The WebView2 SDK package is `Microsoft.Web.WebView2` 1.0.4258.31, distributed under the BSD 3-Clause license. Its exact NuGet `LICENSE.txt` and `NOTICE.txt` are included as `licenses/Microsoft.Web.WebView2-LICENSE.txt` and `licenses/Microsoft.Web.WebView2-NOTICE.txt`. The WebView2 Runtime is a separate Microsoft component, not part of this app's source or binary bundle.

The setup includes Microsoft's official Evergreen WebView2 bootstrapper. If WebView2 is missing, setup downloads and installs the current Evergreen Runtime for the current user from Microsoft. This step requires an Internet connection. The runtime can update itself and may use Microsoft Defender SmartScreen; see [Microsoft's WebView2 distribution documentation](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution) and [privacy statement](https://privacy.microsoft.com/privacystatement).

## Microsoft WebView2 bootstrapper

The setup embeds Microsoft's signed Evergreen bootstrapper (not the Runtime itself). It runs only if no valid WebView2 runtime version is registered and installs for the current user. The End User License Terms are presented in the setup information page; Microsoft's distribution and privacy links are listed in `installer/webview2-terms.txt`.
