# Third party notices

## REPOapi

The HTTP, security and dispatcher layer of `PeakRestApi.Core` is adapted from
[REPOapi](https://github.com/nglmercer/REPOapi), the REST API for
[R.E.P.O.](https://store.steampowered.com/app/2447310/REPO__), by the same
author.

REPOapi is distributed under the MIT licence:

```
MIT License

Copyright (c) 2026 R.E.P.O. REST API contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The files that carry an attribution header are:

* `src/PeakRestApi.Core/Http/RestServer.cs`
* `src/PeakRestApi.Core/Http/ApiRouter.cs`
* `src/PeakRestApi.Core/Http/AuthenticationMiddleware.cs`
* `src/PeakRestApi.Core/Http/ApiResponse.cs`
* `src/PeakRestApi.Core/Game/MainThreadDispatcher.cs`
* `src/PeakRestApi.Core/Security/RateLimiter.cs`
* `src/PeakRestApi.Core/Security/IdempotencyStore.cs`

The game facing parts, the catalog, the spawn services, the configuration and
the whole plugin are new code for PEAK.

## Newtonsoft.Json

PEAKapi uses [Newtonsoft.Json](https://www.newtonsoft.com/json), which is
distributed under the MIT licence. PEAK itself ships the same library, so the
copy in the plugin folder is only a fallback and may be removed.

## Unity and BepInEx

The plugin is compiled against the reference assemblies of
[Unity](https://unity.com/) and [BepInEx](https://github.com/BepInEx/BepInEx).
Both are used at compile time only. The game provides the real assemblies at
runtime; they are not redistributed here.

## PEAK

PEAK is a game by LandCrab. PEAKapi is an unofficial community mod. It is not
affiliated with or endorsed by the developers of PEAK.
