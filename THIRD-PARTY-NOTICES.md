# Third-party notices

The root MIT license applies to owned client source and the original neutral icon.
It does not relicense dependencies, Windows, NSIS, or downloaded official packages.
No third-party application source is vendored into this repository.

## Framework application dependencies

The Framework payload depends on the following Microsoft .NET NuGet packages,
whose installed package metadata identifies MIT licensing: System.Text.Json 10.0.12,
System.Text.Encodings.Web 10.0.12, System.IO.Pipelines 10.0.12,
Microsoft.Bcl.AsyncInterfaces 10.0.12, System.Buffers 4.6.1, System.Memory 4.6.3,
System.Numerics.Vectors 4.6.1, System.Runtime.CompilerServices.Unsafe 6.1.2,
System.Threading.Tasks.Extensions 4.6.3 and System.ValueTuple 4.6.2. ValueTuple resolves to a framework placeholder and is
not redistributed as a DLL. The nine actual DLLs and hashes are recorded in
`build/runtime-inventory.json`.

Copyright (c) .NET Foundation and Contributors. Some packages also retain
Microsoft Corporation copyright notices. See the authoritative package LICENSE.TXT
and THIRD-PARTY-NOTICES.TXT where supplied, and the
[.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT).
The shared permission text is reproduced below; upstream package-specific notices
must accompany redistributed binaries.

## Development and test dependencies

Microsoft.NETFramework.ReferenceAssemblies 1.0.3 and its net48/net481 reference
packs are build-only assets; their package metadata points to the
[Microsoft .NET license](https://github.com/microsoft/dotnet/blob/main/LICENSE).
Microsoft.NET.Test.Sdk 17.14.1, Microsoft.CodeCoverage 17.14.1 and
Microsoft.TestPlatform.* 17.14.1 identify MIT licensing. Newtonsoft.Json 13.0.3
identifies MIT licensing, copyright James Newton-King. Older System.Reflection.Metadata
1.6.0 and System.Collections.Immutable 1.5.0 identify the
[.NET CoreFX license](https://github.com/dotnet/corefx/blob/master/LICENSE.TXT).
These test dependencies are not part of the App payload.

xUnit.net 2.9.3, xunit.analyzers 1.18.0 and xunit.runner.visualstudio 3.1.5 use
Apache License 2.0. xunit.abstractions 2.0.3 retains its package's upstream license
reference. See [xUnit license](https://github.com/xunit/xunit/blob/main/license.txt)
and [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0).
Copyright .NET Foundation and xUnit.net contributors; preserve package notices when
redistributing test tooling. No test tooling is embedded in the installer.

## NSIS and official packages

NSIS 3.12 is an external packaging tool. Its wrapper runtime, standard includes and
plugins keep their original licenses; see the
[NSIS license documentation](https://nsis.sourceforge.io/License) and the selected
NSIS distribution's COPYING/Docs licenses. The compiler and plugins are not
relicensed under the client's MIT license.

The installed Microsoft .NET Framework is a Windows prerequisite, not a distributed
runtime. OpenAI's official Codex/ChatGPT MSIX and license files are downloaded from
the official source under their own terms; no package bytes or licenses are checked
into this repository and the client source license grants no rights to those products.

## MIT permission text for dependencies identified above

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

Exact upstream license texts and source-access instructions are retained in
`licenses/dotnet/` and `licenses/NSIS/` and copied into both payloads.
