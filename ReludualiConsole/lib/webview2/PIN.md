# WebView2 SDK: pinned

- **Package:** `Microsoft.Web.WebView2` **1.0.4258.31** (stable, published 2026-09-28), fetched 2026-10-18 at Mycelium's word
  ("Go ahead with the webview2"), from
  `https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.4258.31/microsoft.web.webview2.1.0.4258.31.nupkg`
  (9,801,922 bytes; SHA-512 `HlGVwvyP/IWiXAU8K57Vmg59khY3DWMOxMnH4AHusFRy0/vDAjKItXUIVNXiXXaq4CLfSZyNUic1wOIlnPLsPQ==`,
  the same as nuget.org's catalog entry).
- **Taken from it** (the Designer is 32-bit, .NET Framework 4.7.2):

  | file | from the package | bytes | SHA-256 (first 16) |
  |---|---|---|---|
  | `Microsoft.Web.WebView2.Core.dll` | `lib/net462/` | 698,208 | `d60e6e94245078cb` |
  | `Microsoft.Web.WebView2.Wpf.dll` | `lib/net462/` | 86,368 | `f05a06bbc03afcb8` |
  | `WebView2Loader.dll` | `build/native/x86/` | 155,488 | `87c61e2cae380112` |
  | `LICENSE.txt`, `NOTICE.txt` | the package's own | | |

- **Licence:** Microsoft's, BSD-style (`LICENSE.txt`): binaries may be redistributed with the copyright notice and
  conditions reproduced in the documentation; it is in `THIRD-PARTY-NOTICES.txt`, which ships with the Designer.
- **The runtime is not here.** WebView2 Evergreen is part of Windows 11 and of any PC running Lumit; the installer checks
  for it and, when it is missing, offers Microsoft's own bootstrapper (the person accepts its licence, never us for them).
  Without it the Designer opens the Camo Editor in the browser instead.
