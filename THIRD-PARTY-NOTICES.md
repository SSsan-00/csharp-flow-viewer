# Third-party notices

| Component | Version / license | Included notice |
| --- | --- | --- |
| .NET runtime, .NET libraries and Windows Desktop (WPF) | Windows x64 runtime 9.0.23 in the verified bundle, MIT | licenses/dotnet-runtime-LICENSE.txt, licenses/dotnet-runtime-NOTICES.txt, licenses/WPF-LICENSE.txt and licenses/WPF-NOTICES.txt |
| Roslyn compiler and workspaces (including the build hosts) | 4.14.0, MIT | licenses/Roslyn-LICENSE.txt and licenses/Roslyn-NOTICES.rtf |
| MSBuild Framework and StringTools | 17.14.28, MIT | licenses/MSBuild-LICENSE.txt and licenses/MSBuild-NOTICES.txt |
| AvalonEdit | 6.3.1.120, MIT | licenses/AvalonEdit.txt |
| WebView2 SDK, including the native loader | 1.0.3650.58, BSD 3-Clause | licenses/WebView2-SDK.txt |
| Windows SDK for .NET projections and WinRT runtime | Microsoft.Windows.SDK.NET.Ref 10.0.17763.57, Microsoft Windows SDK license | [License terms](https://aka.ms/WinSDKLicenseURL) and [redistributable files](https://learn.microsoft.com/legal/windows-sdk/redist#microsoftwindowssdknetref) |
| Humanizer.Core | 2.14.1, MIT | licenses/Humanizer.txt |
| Newtonsoft.Json | 13.0.3, MIT | licenses/Newtonsoft.Json.txt |
| Mermaid and modules linked into the browser bundle | Mermaid 12.3.0, MIT. Modules carry their own license terms | tools/preview/THIRD-PARTY-LICENSES.txt |

The WebView2 Evergreen Runtime and the target project's .NET SDK / MSBuild installation are obtained separately from their distributors. Development-only npm tooling and MSTest are outside the application bundle.

The Windows executable embeds these notices, accessible from the **診断** button’s context menu under **ライセンスと第三者通知**. The Mermaid notice is generated from the actual esbuild input list. When changing the .NET runtime version used for publication, update its notices from the corresponding runtime packages and WPF release.
