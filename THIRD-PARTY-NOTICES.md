# Third-party notices

DS5 Untethered is licensed under GPL-3.0-or-later (see `LICENSE`). A published build of DS5 Untethered includes the following
third-party components. License types were taken from each package's NuGet metadata (`.nuspec`) and bundled
license files.

## NuGet packages shipped with the application

| Component | Version | License | Copyright | Project |
|---|---|---|---|---|
| Avalonia | 11.3.22 | MIT | Copyright 2013-2026 © The AvaloniaUI Project (AvaloniaUI OÜ) | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Desktop | 11.3.22 | MIT | Copyright 2013-2026 © The AvaloniaUI Project (AvaloniaUI OÜ) | https://github.com/AvaloniaUI/Avalonia |
| Semi.Avalonia | 11.3.22 | MIT | IRIHI Technology Co., Ltd. | https://github.com/irihitech/Semi.Avalonia |
| Avalonia.Fonts.Inter | 11.3.22 | MIT (package code); the embedded Inter font is SIL Open Font License 1.1 | Package: The AvaloniaUI Project. Font: Copyright (c) 2016 The Inter Project Authors (Rasmus Andersson) | https://github.com/AvaloniaUI/Avalonia, https://github.com/rsms/inter |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | Copyright © .NET Foundation and Contributors | https://github.com/CommunityToolkit/dotnet |
| Concentus (managed Opus codec; experimental controller speaker) | 2.2.2 | BSD-3-Clause (text below) | Copyright (c) by various holding parties, including (but not limited to): Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation, Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry, Logan Stromberg (Concentus author). The Opus reference implementation it ports: Xiph.Org Foundation and the Opus contributors | https://github.com/lostromb/concentus, https://opus-codec.org |

Transitive packages pulled in by Avalonia and shipped with it:

| Component | Version | License | Copyright | Project |
|---|---|---|---|---|
| Avalonia.Win32, Avalonia.Skia, Avalonia.Remote.Protocol and related Avalonia.* packages | 11.3.22 | MIT | The AvaloniaUI Project | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Angle.Windows.Natives (ANGLE) | 2.1.25547.20250602 | BSD-3-Clause style (ANGLE license, bundled `LICENSE`) | Copyright 2018 The ANGLE Project Authors | https://chromium.googlesource.com/angle/angle |
| SkiaSharp, SkiaSharp.NativeAssets.Win32 | 2.88.9 | MIT (see the package's THIRD-PARTY-NOTICES.txt for Skia and its dependencies) | © Microsoft Corporation | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp, HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.1 | MIT (see the package's THIRD-PARTY-NOTICES.txt for HarfBuzz) | © Microsoft Corporation | https://github.com/mono/SkiaSharp |
| MicroCom.Runtime | 0.11.0 | MIT | Copyright 2021 © Nikita Tsukanov | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol | 0.21.3 | MIT | Tom Deseyn | https://github.com/tmds/Tmds.DBus |
| Irihi.Avalonia.Shared (pulled in by Semi.Avalonia) | 0.3.1 | MIT | IRIHI Technology Co., Ltd. | https://github.com/irihitech/Ursa.Avalonia |
| System.IO.Pipelines | 8.0.0 | MIT | © Microsoft Corporation | https://github.com/dotnet/runtime |

## .NET runtime (self-contained builds only)

If DS5 Untethered is published self-contained, the .NET 8 runtime is included. It is licensed under MIT,
Copyright (c) .NET Foundation and Contributors (https://github.com/dotnet/runtime). The runtime carries its own
`ThirdPartyNotices.txt`, which applies to the files it ships.

## Development-only packages (not shipped)

xunit, xunit.runner.visualstudio, coverlet.collector, Microsoft.NET.Test.Sdk and Avalonia.Headless.XUnit are used only
to build and run the tests and are not part of a published build.

## External tools (not bundled)

DS5 Untethered does not include these tools. It only calls them after the user has installed them from their official pages.

| Tool | License | Copyright | Project |
|---|---|---|---|
| usbip-win2 | BSD-2-Clause (repository `LICENSE.txt`) | Copyright (c) 2021-2026, Vadym Hrynchyshyn | https://github.com/vadimgrn/usbip-win2 |
| HidHide | MIT | Nefarius Software Solutions e.U. | https://github.com/nefarius/HidHide |

## Controller image

`src/Kopru.App/Assets/controller*.png` come from an illustration supplied by the project author (background removed,
light strip split into a mask, dark variant derived). It carries no logos.

## MIT License

The following text applies to every component listed above as MIT, with that component's copyright line.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## SIL Open Font License 1.1 (Inter font)

Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter)

```
This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
https://openfontlicense.org


-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```

## Concentus / Opus license (BSD-3-Clause)

From the Concentus 2.2.2 package's `LICENSE` file:

```
Copyright (c) by various holding parties, including (but not limited to): 
Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation,
Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Optional, not shipped

- libopus (`opus.dll`), BSD-3-Clause, Copyright Xiph.Org Foundation and contributors, https://opus-codec.org - a native
  alternative to Concentus for the experimental controller speaker, used only when `KOPRU_OPUS=native` is set and the
  user put an `opus.dll` next to the program (`NativeOpusEncoder`). No build ships it.

